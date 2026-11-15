#!/usr/bin/env python3
"""
Writes Data/packs/<id>/pack.json and sources.json from the compact lists below, so every pack uses the same shape. Re-run after editing a
list, then run check_feeds.py --prune on the result. (Glossary, tools, tips and learning paths are written by hand in their own files.)
"""
import json, os

ROOT = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "Data", "packs")

# (name, url, category, content type, level, tags, note)
N, T, C, R = "News", "Tools", "Community", "Release"

PACKS = {
    "web": {
        "name": "Web & JavaScript", "topic": "Web",
        "description": "Front-end and Node.js: browsers, frameworks, CSS, TypeScript and the tools around them.",
        "sources": [
            ("CSS-Tricks", "https://css-tricks.com/feed/", N, "Tutorial", "Intermediate", ["CSS"], "Tips and deep dives on CSS and front-end craft."),
            ("Smashing Magazine", "https://www.smashingmagazine.com/feed/", N, "Tutorial", "Intermediate", ["Web"], "Long-form articles on design, UX and front-end engineering."),
            ("web.dev", "https://web.dev/feed.xml", N, "Tutorial", "Intermediate", ["Web", "Performance"], "Google's guidance on modern web platform features and performance."),
            ("Chrome for Developers", "https://developer.chrome.com/static/blog/feed.xml", N, "News", "Intermediate", ["Web"], "What is new in Chrome and the web platform."),
            ("MDN Blog", "https://developer.mozilla.org/en-US/blog/rss.xml", N, "News", "Intermediate", ["Web"], "Updates from the MDN Web Docs team."),
            ("A List Apart", "https://alistapart.com/main/feed/", N, "Tutorial", "Intermediate", ["Web"], "Articles on web standards and design."),
            ("JavaScript Weekly", "https://javascriptweekly.com/rss/", N, "News", "Intermediate", ["JavaScript"], "A weekly roundup of JavaScript news and tutorials."),
            ("Node Weekly", "https://nodeweekly.com/rss/", N, "News", "Intermediate", ["Node.js"], "A weekly roundup for Node.js developers."),
            ("Frontend Focus", "https://frontendfoc.us/rss", N, "News", "Intermediate", ["Web"], "A weekly front-end newsletter."),
            ("TypeScript blog", "https://devblogs.microsoft.com/typescript/feed/", N, "News", "Intermediate", ["TypeScript"], "Release notes and design notes from the TypeScript team."),
            ("Node.js blog", "https://nodejs.org/en/feed/blog.xml", N, "News", "Intermediate", ["Node.js"], "Official Node.js announcements and security releases."),
            ("React blog", "https://react.dev/rss.xml", N, "News", "Intermediate", ["React"], "Official React announcements."),
            ("Vue blog", "https://blog.vuejs.org/feed.rss", N, "News", "Intermediate", ["Vue"], "Official Vue.js announcements."),
            ("Svelte blog", "https://svelte.dev/blog/rss.xml", N, "News", "Intermediate", ["Svelte"], "Official Svelte announcements."),
            ("Overreacted", "https://overreacted.io/rss.xml", C, "Tutorial", "Advanced", ["React"], "Dan Abramov on how React and modern JavaScript really work."),
            ("Josh Comeau", "https://www.joshwcomeau.com/rss.xml", C, "Tutorial", "Intermediate", ["CSS", "React"], "Interactive, beautifully explained front-end tutorials."),
            ("Dev.to · JavaScript", "https://dev.to/feed/tag/javascript", C, "News", "Beginner", ["JavaScript"], "Community posts tagged JavaScript."),
            ("Dev.to · webdev", "https://dev.to/feed/tag/webdev", C, "News", "Beginner", ["Web"], "Community posts tagged webdev."),
            ("Next.js releases", "https://github.com/vercel/next.js/releases.atom", T, "Release", "Intermediate", ["React"], "New Next.js versions."),
            ("Vite releases", "https://github.com/vitejs/vite/releases.atom", T, "Release", "Intermediate", ["Vite"], "New Vite versions."),
            ("TypeScript releases", "https://github.com/microsoft/TypeScript/releases.atom", T, "Release", "Intermediate", ["TypeScript"], "New TypeScript versions."),
            ("React releases", "https://github.com/facebook/react/releases.atom", T, "Release", "Intermediate", ["React"], "New React versions."),
            ("Node.js releases", "https://github.com/nodejs/node/releases.atom", T, "Release", "Intermediate", ["Node.js"], "New Node.js versions."),
            ("Deno releases", "https://github.com/denoland/deno/releases.atom", T, "Release", "Intermediate", ["Deno"], "New Deno versions."),
            ("Astro blog", "https://astro.build/rss.xml", N, "News", "Intermediate", ["Astro"], "Official Astro announcements."),
        ],
    },
    "dotnet": {
        "name": ".NET & C#", "topic": ".NET",
        "description": "The .NET platform, C#, ASP.NET Core, Entity Framework and the community around them.",
        "sources": [
            (".NET Blog", "https://devblogs.microsoft.com/dotnet/feed/", N, "News", "Intermediate", [".NET"], "Official .NET announcements and release notes."),
            ("Visual Studio Blog", "https://devblogs.microsoft.com/visualstudio/feed/", N, "News", "Intermediate", ["Visual Studio"], "Visual Studio and tooling news."),
            ("JetBrains .NET Tools Blog", "https://blog.jetbrains.com/dotnet/feed/", N, "News", "Intermediate", [".NET"], "Rider, ReSharper and .NET community posts."),
            ("Andrew Lock", "https://andrewlock.net/rss.xml", C, "Tutorial", "Advanced", ["ASP.NET Core"], "Deep dives into ASP.NET Core internals."),
            ("Scott Hanselman", "https://feeds.hanselman.com/ScottHanselman", C, "News", "Beginner", [".NET"], "Tips, tools and stories from a .NET veteran."),
            ("Steve Gordon", "https://www.stevejgordon.co.uk/feed", C, "Tutorial", "Advanced", [".NET", "Performance"], "Performance and ASP.NET Core articles."),
            ("Khalid Abuhakmeh", "https://khalidabuhakmeh.com/feed.xml", C, "Tutorial", "Intermediate", [".NET"], "Practical .NET tips and experiments."),
            ("Code Maze", "https://code-maze.com/feed/", C, "Tutorial", "Beginner", ["C#", "ASP.NET Core"], "Step-by-step C# and ASP.NET Core tutorials."),
            ("Dev.to · dotnet", "https://dev.to/feed/tag/dotnet", C, "News", "Beginner", [".NET"], "Community posts tagged dotnet."),
            ("Dev.to · csharp", "https://dev.to/feed/tag/csharp", C, "News", "Beginner", ["C#"], "Community posts tagged csharp."),
            (".NET runtime releases", "https://github.com/dotnet/runtime/releases.atom", T, "Release", "Intermediate", [".NET"], "New .NET runtime versions."),
            ("ASP.NET Core releases", "https://github.com/dotnet/aspnetcore/releases.atom", T, "Release", "Intermediate", ["ASP.NET Core"], "New ASP.NET Core versions."),
            ("EF Core releases", "https://github.com/dotnet/efcore/releases.atom", T, "Release", "Intermediate", ["EF Core"], "New Entity Framework Core versions."),
            (".NET SDK releases", "https://github.com/dotnet/sdk/releases.atom", T, "Release", "Intermediate", [".NET"], "New .NET SDK versions."),
            (".NET MAUI releases", "https://github.com/dotnet/maui/releases.atom", T, "Release", "Intermediate", [".NET MAUI"], "New .NET MAUI versions."),
            ("Serilog releases", "https://github.com/serilog/serilog/releases.atom", T, "Release", "Intermediate", ["Logging"], "New Serilog versions."),
            ("xUnit releases", "https://github.com/xunit/xunit/releases.atom", T, "Release", "Intermediate", ["Testing"], "New xUnit versions."),
        ],
    },
    "python": {
        "name": "Python", "topic": "Python",
        "description": "Python the language, its web and data ecosystems, and the tooling that keeps changing.",
        "sources": [
            ("Python Insider", "https://pythoninsider.blogspot.com/feeds/posts/default", N, "News", "Intermediate", ["Python"], "Official Python release announcements."),
            ("Real Python", "https://realpython.com/atom.xml", N, "Tutorial", "Beginner", ["Python"], "Tutorials from beginner to advanced."),
            ("Planet Python", "https://planetpython.org/rss20.xml", C, "News", "Intermediate", ["Python"], "An aggregate of Python community blogs."),
            ("PyCoder's Weekly", "https://pycoders.com/feed", N, "News", "Intermediate", ["Python"], "A weekly roundup of Python news and articles."),
            ("Python Bytes", "https://pythonbytes.fm/episodes/rss", C, "News", "Beginner", ["Python"], "Short weekly podcast on Python news."),
            ("Talk Python To Me", "https://talkpython.fm/episodes/rss", C, "News", "Intermediate", ["Python"], "Podcast interviews across the Python world."),
            ("Ned Batchelder", "https://nedbatchelder.com/blog/rss.xml", C, "Tutorial", "Advanced", ["Python", "Testing"], "Thoughtful posts on Python, testing and coverage."),
            ("Trey Hunner", "https://treyhunner.com/atom.xml", C, "Tutorial", "Intermediate", ["Python"], "Idiomatic Python explained clearly."),
            ("Python Speed", "https://pythonspeed.com/atom.xml", C, "Tutorial", "Advanced", ["Python", "Performance"], "Making Python faster, and packaging it for production."),
            ("Django weblog", "https://www.djangoproject.com/rss/weblog/", N, "News", "Intermediate", ["Django"], "Official Django announcements and security releases."),
            ("Dev.to · python", "https://dev.to/feed/tag/python", C, "News", "Beginner", ["Python"], "Community posts tagged python."),
            ("uv releases", "https://github.com/astral-sh/uv/releases.atom", T, "Release", "Intermediate", ["Packaging"], "New uv versions."),
            ("Ruff releases", "https://github.com/astral-sh/ruff/releases.atom", T, "Release", "Intermediate", ["Linting"], "New Ruff versions."),
            ("FastAPI releases", "https://github.com/fastapi/fastapi/releases.atom", T, "Release", "Intermediate", ["FastAPI"], "New FastAPI versions."),
            ("Pydantic releases", "https://github.com/pydantic/pydantic/releases.atom", T, "Release", "Intermediate", ["Pydantic"], "New Pydantic versions."),
            ("Flask releases", "https://github.com/pallets/flask/releases.atom", T, "Release", "Intermediate", ["Flask"], "New Flask versions."),
            ("pytest releases", "https://github.com/pytest-dev/pytest/releases.atom", T, "Release", "Intermediate", ["Testing"], "New pytest versions."),
            ("pandas releases", "https://github.com/pandas-dev/pandas/releases.atom", T, "Release", "Intermediate", ["Data"], "New pandas versions."),
        ],
    },
    "devops": {
        "name": "DevOps & Cloud", "topic": "DevOps",
        "description": "Containers, Kubernetes, infrastructure as code, observability and the major clouds.",
        "sources": [
            ("Kubernetes blog", "https://kubernetes.io/feed.xml", N, "News", "Intermediate", ["Kubernetes"], "Official Kubernetes announcements."),
            ("Docker blog", "https://www.docker.com/blog/feed/", N, "News", "Intermediate", ["Docker"], "Docker product news and guides."),
            ("HashiCorp blog", "https://www.hashicorp.com/blog/feed.xml", N, "News", "Intermediate", ["Terraform"], "Terraform, Vault, Consul and Nomad news."),
            ("CNCF blog", "https://www.cncf.io/feed/", N, "News", "Intermediate", ["Cloud Native"], "Cloud Native Computing Foundation news."),
            ("AWS News Blog", "https://aws.amazon.com/blogs/aws/feed/", N, "News", "Intermediate", ["AWS"], "Launches and updates from AWS."),
            ("Google Cloud blog", "https://cloudblog.withgoogle.com/rss/", N, "News", "Intermediate", ["Google Cloud"], "Google Cloud announcements and how-tos."),
            ("Azure blog", "https://azure.microsoft.com/en-us/blog/feed/", N, "News", "Intermediate", ["Azure"], "Microsoft Azure announcements."),
            ("The New Stack", "https://thenewstack.io/feed/", N, "News", "Intermediate", ["DevOps"], "News and analysis on cloud native and DevOps."),
            ("Cloudflare blog", "https://blog.cloudflare.com/rss/", N, "News", "Advanced", ["Networking"], "Engineering posts from Cloudflare."),
            ("GitHub blog", "https://github.blog/feed/", N, "News", "Intermediate", ["GitHub"], "GitHub product and engineering news."),
            ("GitLab blog", "https://about.gitlab.com/atom.xml", N, "News", "Intermediate", ["CI/CD"], "GitLab DevSecOps news."),
            ("Julia Evans", "https://jvns.ca/atom.xml", C, "Tutorial", "Beginner", ["Linux", "Networking"], "Wonderfully clear explanations of systems topics."),
            ("Brendan Gregg", "https://www.brendangregg.com/blog/rss.xml", C, "Tutorial", "Advanced", ["Performance"], "Systems performance and observability."),
            ("Netflix Tech Blog", "https://netflixtechblog.com/feed", C, "News", "Advanced", ["Architecture"], "Engineering at scale."),
            ("SRE Weekly", "https://sreweekly.com/feed/", C, "News", "Intermediate", ["SRE"], "A weekly digest of reliability and incident articles."),
            ("Kubernetes releases", "https://github.com/kubernetes/kubernetes/releases.atom", T, "Release", "Intermediate", ["Kubernetes"], "New Kubernetes versions."),
            ("Terraform releases", "https://github.com/hashicorp/terraform/releases.atom", T, "Release", "Intermediate", ["Terraform"], "New Terraform versions."),
            ("OpenTofu releases", "https://github.com/opentofu/opentofu/releases.atom", T, "Release", "Intermediate", ["Terraform"], "New OpenTofu versions."),
            ("Helm releases", "https://github.com/helm/helm/releases.atom", T, "Release", "Intermediate", ["Kubernetes"], "New Helm versions."),
            ("Docker Compose releases", "https://github.com/docker/compose/releases.atom", T, "Release", "Intermediate", ["Docker"], "New Docker Compose versions."),
            ("Prometheus releases", "https://github.com/prometheus/prometheus/releases.atom", T, "Release", "Intermediate", ["Observability"], "New Prometheus versions."),
            ("Grafana releases", "https://github.com/grafana/grafana/releases.atom", T, "Release", "Intermediate", ["Observability"], "New Grafana versions."),
        ],
    },
    "security": {
        "name": "Security", "topic": "Security",
        "description": "Application security, vulnerabilities and breaches, and the practices that keep systems safe.",
        "sources": [
            ("Krebs on Security", "https://krebsonsecurity.com/feed/", N, "News", "Beginner", ["Security"], "Investigative reporting on cybercrime."),
            ("Schneier on Security", "https://www.schneier.com/feed/atom/", C, "News", "Intermediate", ["Security"], "Bruce Schneier on security and society."),
            ("The Hacker News", "https://feeds.feedburner.com/TheHackersNews", N, "News", "Beginner", ["Security"], "Daily cybersecurity news."),
            ("BleepingComputer", "https://www.bleepingcomputer.com/feed/", N, "News", "Beginner", ["Security"], "Security news and malware coverage."),
            ("Dark Reading", "https://www.darkreading.com/rss.xml", N, "News", "Intermediate", ["Security"], "Enterprise security news."),
            ("Troy Hunt", "https://www.troyhunt.com/rss/", C, "News", "Beginner", ["Security", "Breaches"], "Have I Been Pwned's creator on breaches and the web."),
            ("PortSwigger Research", "https://portswigger.net/research/rss", N, "Tutorial", "Advanced", ["Web Security"], "New web attack techniques from the Burp Suite makers."),
            ("Google Project Zero", "https://googleprojectzero.blogspot.com/feeds/posts/default", N, "Tutorial", "Advanced", ["Vulnerabilities"], "In-depth vulnerability research."),
            ("Trail of Bits blog", "https://blog.trailofbits.com/feed/", N, "Tutorial", "Advanced", ["Security"], "Security engineering and research."),
            ("CISA advisories", "https://www.cisa.gov/cybersecurity-advisories/all.xml", N, "News", "Intermediate", ["Advisories"], "US government security advisories and alerts."),
            ("Rapid7 blog", "https://blog.rapid7.com/rss/", N, "News", "Intermediate", ["Security"], "Threat research and security guidance."),
            ("Dev.to · security", "https://dev.to/feed/tag/security", C, "News", "Beginner", ["Security"], "Community posts tagged security."),
            ("Trivy releases", "https://github.com/aquasecurity/trivy/releases.atom", T, "Release", "Intermediate", ["Scanning"], "New Trivy versions."),
            ("OpenSSL releases", "https://github.com/openssl/openssl/releases.atom", T, "Release", "Advanced", ["Cryptography"], "New OpenSSL versions."),
            ("ZAP releases", "https://github.com/zaproxy/zaproxy/releases.atom", T, "Release", "Intermediate", ["Web Security"], "New OWASP ZAP versions."),
        ],
    },
}


def main():
    for pid, pack in PACKS.items():
        folder = os.path.join(ROOT, pid)
        os.makedirs(folder, exist_ok=True)
        with open(os.path.join(folder, "pack.json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump({"name": pack["name"], "topic": pack["topic"], "description": pack["description"]}, f, indent=2, ensure_ascii=False)
            f.write("\n")
        rows = [
            {"Name": n, "Url": u, "Category": c, "Note": note, "ContentType": ct, "Level": lv, "Tags": tags}
            for (n, u, c, ct, lv, tags, note) in pack["sources"]
        ]
        with open(os.path.join(folder, "sources.json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump(rows, f, indent=2, ensure_ascii=False)
            f.write("\n")
        print(pid, len(rows), "sources")


if __name__ == "__main__":
    main()
