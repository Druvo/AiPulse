"""
Headless-Edge QA harness for the UI (no Playwright needed): screenshots, horizontal-overflow check, console errors and a WCAG AA
contrast audit for any page, in light/dark and at any viewport, optionally signed in.

  python tools/qa/shot.py --base http://127.0.0.1:5301 --out shots --paths / /news /explore --width 390 --height 844 --theme dark
  python tools/qa/shot.py --base http://127.0.0.1:5301 --user admin --password <test password> --paths /dashboard /settings

Sign-in is a plain form POST to /login (what a browser would send) with the credentials you pass. Use it only against a LOCAL instance
with its own throwaway App_Data and a throwaway test password. Prints one JSON line per page:
{path, title, theme, w, overflow:{...}, consoleErrors:[...], contrast:{failing,worst}, file}.
"""
import argparse, asyncio, base64, json, os, re, subprocess, sys, tempfile, time, urllib.parse, urllib.request, http.cookiejar
import websockets

EDGE = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"

AUDIT = r"""(() => {
  const ctx = document.createElement('canvas').getContext('2d', { willReadFrequently: true });
  const parse = c => { ctx.clearRect(0,0,1,1); ctx.fillStyle='#000'; ctx.fillStyle=c; ctx.fillRect(0,0,1,1); const d=ctx.getImageData(0,0,1,1).data; return [d[0],d[1],d[2],d[3]/255]; };
  const over = (t,b) => { const a=t[3]+b[3]*(1-t[3]); if(a===0) return [0,0,0,0]; return [0,1,2].map(i=>(t[i]*t[3]+b[i]*b[3]*(1-t[3]))/a).concat(a); };
  const lum = ([r,g,b]) => { const f=v=>{v/=255; return v<=0.03928? v/12.92: Math.pow((v+0.055)/1.055,2.4)}; return 0.2126*f(r)+0.7152*f(g)+0.0722*f(b); };
  const ratio = (a,b)=>{const la=lum(a),lb=lum(b); return (Math.max(la,lb)+0.05)/(Math.min(la,lb)+0.05)};
  const bgOf = el => { let L=[]; for(let e=el;e;e=e.parentElement){const c=parse(getComputedStyle(e).backgroundColor); if(c[3]>0) L.push(c); if(c[3]>=1) break;} let r=parse(getComputedStyle(document.documentElement).backgroundColor); if(r[3]<1) r=[255,255,255,1]; for(let i=L.length-1;i>=0;i--) r=over(L[i],r); return r; };
  const vis = el => { const cs=getComputedStyle(el); if(cs.visibility==='hidden'||cs.display==='none'||parseFloat(cs.opacity)===0) return false; const r=el.getBoundingClientRect(); return r.width>0&&r.height>0; };
  const F=new Map(); let checked=0;
  for (const el of document.body.querySelectorAll('*')) {
    if (['SCRIPT','STYLE','SVG','PATH','NOSCRIPT'].includes(el.tagName.toUpperCase())) continue;
    if (el.closest('[disabled], [aria-hidden="true"], svg, .disabled')) continue;
    const own=[...el.childNodes].filter(n=>n.nodeType===3&&n.textContent.trim().length>0);
    if(!own.length||!vis(el)) continue;
    const cs=getComputedStyle(el); const bg=bgOf(el); let fg=parse(cs.color); fg=over([fg[0],fg[1],fg[2],fg[3]*parseFloat(cs.opacity)],bg);
    const size=parseFloat(cs.fontSize), bold=parseInt(cs.fontWeight,10)>=700, large=size>=24||(size>=18.66&&bold), need=large?3:4.5, got=ratio(fg,bg); checked++;
    if(got<need){ const k=el.tagName.toLowerCase()+'.'+String(el.className||'').split(/\s+/).slice(0,3).join('.'); if(!F.has(k)) F.set(k,{selector:k,ratio:+got.toFixed(2),need,text:own[0].textContent.trim().slice(0,40),count:0}); F.get(k).count++; }
  }
  return {checked, failing:F.size, worst:[...F.values()].sort((a,b)=>a.ratio-b.ratio).slice(0,6)};
})()"""

OVERFLOW = r"""(() => { const de=document.documentElement; const w=de.clientWidth; const bad=[...document.body.querySelectorAll('*')].filter(e=>{const r=e.getBoundingClientRect(); const cs=getComputedStyle(e); return r.width>0 && r.right>w+1 && cs.position!=='fixed' && !e.closest('.table-responsive,pre,[style*="overflow"],.overflow-auto,.overflow-x-auto');}).slice(0,4).map(e=>e.tagName.toLowerCase()+'.'+String(e.className).split(/\s+/).slice(0,2).join('.')); return {scrollWidth:de.scrollWidth, clientWidth:w, overflow: de.scrollWidth>w+1, offenders:bad}; })()"""


def login_cookies(base, user, password):
    jar = http.cookiejar.CookieJar()
    op = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
    html = op.open(base + "/login").read().decode()
    token = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', html) or re.search(r'value="([^"]+)"[^>]*name="__RequestVerificationToken"', html)
    form = {"Input.Username": user, "Input.Password": password, "Input.Remember": "true", "__RequestVerificationToken": token.group(1) if token else ""}
    for m in re.finditer(r'<input[^>]+name="_handler"[^>]*value="([^"]*)"', html):
        form["_handler"] = m.group(1)
    form[":formname"] = "login"
    try:
        op.open(urllib.request.Request(base + "/login", data=urllib.parse.urlencode(form).encode()))
    except Exception as e:
        print("login post:", e, file=sys.stderr)
    host = urllib.parse.urlparse(base).hostname
    return [{"name": c.name, "value": c.value, "domain": host, "path": "/"} for c in jar]


async def run(a):
    # A free port chosen by the OS: a pid-derived one can land in a Windows-reserved range or collide with a leftover browser.
    import socket
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    prof = tempfile.mkdtemp(prefix="qa-edge-")
    proc = subprocess.Popen([EDGE, "--headless=new", "--disable-gpu", f"--remote-debugging-port={port}", f"--user-data-dir={prof}",
                             "--no-first-run", "--hide-scrollbars", "about:blank"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        ws_url = None
        for _ in range(60):
            try:
                tabs = json.load(urllib.request.urlopen(f"http://127.0.0.1:{port}/json"))
                ws_url = next(t["webSocketDebuggerUrl"] for t in tabs if t["type"] == "page")
                break
            except Exception:
                time.sleep(.5)
        if not ws_url:
            raise SystemExit("Edge did not start")
        cookies = login_cookies(a.base, a.user, a.password) if a.user else []
        os.makedirs(a.out, exist_ok=True)
        async with websockets.connect(ws_url, max_size=64 * 1024 * 1024) as ws:
            counter = 0
            events = []

            async def send(method, params=None):
                nonlocal counter
                counter += 1
                mid = counter
                await ws.send(json.dumps({"id": mid, "method": method, "params": params or {}}))
                while True:
                    msg = json.loads(await ws.recv())
                    if msg.get("id") == mid:
                        return msg.get("result", {})
                    if "method" in msg:
                        events.append(msg)

            async def ev(expr):
                r = await send("Runtime.evaluate", {"expression": expr, "returnByValue": True, "awaitPromise": True})
                return r.get("result", {}).get("value")

            for m in ("Page.enable", "Runtime.enable", "Network.enable", "Log.enable"):
                await send(m)
            for c in cookies:
                await send("Network.setCookie", c)
            await send("Emulation.setDeviceMetricsOverride", {"width": a.width, "height": a.height, "deviceScaleFactor": 1, "mobile": a.width < 768})
            await send("Emulation.setEmulatedMedia", {"features": [{"name": "prefers-color-scheme", "value": a.theme}]})
            await send("Page.addScriptToEvaluateOnNewDocument", {"source": f"try{{localStorage.setItem('aipulse-theme','{a.theme}')}}catch(e){{}}"})
            for path in a.paths:
                events.clear()
                await send("Page.navigate", {"url": a.base + path})
                await asyncio.sleep(a.wait)
                ov = await ev(OVERFLOW)
                ct = await ev(AUDIT)
                errs = [e["params"]["entry"]["text"][:140] for e in events if e.get("method") == "Log.entryAdded" and e["params"]["entry"]["level"] == "error"]
                errs += [e["params"]["exceptionDetails"]["text"][:140] for e in events if e.get("method") == "Runtime.exceptionThrown"]
                title = await ev("document.title")
                shot = await send("Page.captureScreenshot", {"format": "png", "captureBeyondViewport": bool(a.full)})
                name = re.sub(r"[^A-Za-z0-9]+", "_", path).strip("_") or "home"
                fn = os.path.join(a.out, f"{name}_{a.theme}_{a.width}.png")
                open(fn, "wb").write(base64.b64decode(shot["data"]))
                print(json.dumps({"path": path, "title": title, "theme": a.theme, "w": a.width, "overflow": ov, "consoleErrors": errs[:5], "contrast": ct, "file": fn}))
    finally:
        proc.terminate()


p = argparse.ArgumentParser()
p.add_argument("--base", required=True)
p.add_argument("--paths", nargs="+", default=["/"])
p.add_argument("--out", default="qa-shots")
p.add_argument("--width", type=int, default=1280)
p.add_argument("--height", type=int, default=800)
p.add_argument("--theme", default="light", choices=["light", "dark"])
p.add_argument("--user")
p.add_argument("--password")
p.add_argument("--wait", type=float, default=3.0)
p.add_argument("--full", action="store_true")
asyncio.run(run(p.parse_args()))
