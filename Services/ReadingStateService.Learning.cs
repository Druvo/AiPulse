namespace AiPulse.Services;

public sealed partial class ReadingStateService
{
    public bool IsModuleComplete(string title)
    {
        EnsureLoaded();
        lock (_lock) return _state!.CompletedModules.Contains(title);
    }

    public int CompletedModuleCount
    {
        get { EnsureLoaded(); lock (_lock) return _state!.CompletedModules.Count; }
    }

    public bool IsStepComplete(string moduleTitle, int stepIndex)
    {
        EnsureLoaded();
        lock (_lock) return _state!.CompletedSteps.Contains(StepKey(moduleTitle, stepIndex));
    }

    public int CompletedStepCount(string moduleTitle, int totalSteps)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var n = 0;
            for (var i = 0; i < totalSteps; i++)
                if (_state!.CompletedSteps.Contains(StepKey(moduleTitle, i))) n++;
            return n;
        }
    }

    /// <summary>Toggles one step and returns its new state. Recomputes the module's overall completion (and appends a <see cref="Models.ModuleProgressEvent"/> on the transition into fully complete) so the whole-module flag stays derived from steps rather than a second source of truth.</summary>
    public bool ToggleStep(string moduleTitle, int stepIndex, int totalSteps)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var key = StepKey(moduleTitle, stepIndex);
            var nowComplete = !_state!.CompletedSteps.Contains(key);
            if (nowComplete) _state.CompletedSteps.Add(key);
            else _state.CompletedSteps.Remove(key);

            if (nowComplete)
                _state.LearningHistory.Add(new Models.ModuleProgressEvent { ModuleTitle = moduleTitle });

            RecomputeModuleCompletion(moduleTitle, totalSteps);
            Save();
            return nowComplete;
        }
    }

    /// <summary>Marks every step of a module complete in one action - a shortcut for material you already know, so per-step tracking never becomes a chore for the common case.</summary>
    public void MarkAllStepsComplete(string moduleTitle, int totalSteps)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var addedAny = false;
            for (var i = 0; i < totalSteps; i++)
            {
                var key = StepKey(moduleTitle, i);
                if (_state!.CompletedSteps.Add(key)) addedAny = true;
            }
            if (addedAny)
                _state!.LearningHistory.Add(new Models.ModuleProgressEvent { ModuleTitle = moduleTitle });
            RecomputeModuleCompletion(moduleTitle, totalSteps);
            Save();
        }
    }

    /// <summary>Caller holds _lock. Keeps CompletedModules/ModuleCompletedAt in sync with the step-level detail above.</summary>
    private void RecomputeModuleCompletion(string moduleTitle, int totalSteps)
    {
        var allDone = totalSteps > 0 && CompletedStepCountUnlocked(moduleTitle, totalSteps) == totalSteps;
        if (allDone)
        {
            if (_state!.CompletedModules.Add(moduleTitle))
                _state.ModuleCompletedAt[moduleTitle] = DateTimeOffset.Now;
        }
        else
        {
            _state!.CompletedModules.Remove(moduleTitle);
            _state.ModuleCompletedAt.Remove(moduleTitle);
        }
    }

    private int CompletedStepCountUnlocked(string moduleTitle, int totalSteps)
    {
        var n = 0;
        for (var i = 0; i < totalSteps; i++)
            if (_state!.CompletedSteps.Contains(StepKey(moduleTitle, i))) n++;
        return n;
    }

    private static string StepKey(string moduleTitle, int stepIndex) => $"{moduleTitle}::{stepIndex}";

    /// <summary>When each module was completed - for the spaced-repetition "worth a refresher" shelf. A module completed before this field existed (via the old whole-module toggle) has no entry here even though it's in CompletedModules; treated as "no completion date on record" rather than guessed.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> ModuleCompletedAt
    {
        get { EnsureLoaded(); lock (_lock) return new Dictionary<string, DateTimeOffset>(_state!.ModuleCompletedAt); }
    }

    /// <summary>Append-only log of step completions - powers the Learning Hub's own streak and time-invested total.</summary>
    public IReadOnlyList<Models.ModuleProgressEvent> LearningHistory
    {
        get { EnsureLoaded(); lock (_lock) return _state!.LearningHistory.ToList(); }
    }

    public string GetModuleNote(string moduleTitle)
    {
        EnsureLoaded();
        lock (_lock) return _state!.ModuleNotes.GetValueOrDefault(moduleTitle, "");
    }

    public void SetModuleNote(string moduleTitle, string note)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(note)) _state!.ModuleNotes.Remove(moduleTitle);
            else _state!.ModuleNotes[moduleTitle] = note.Trim();
            Save();
        }
    }

    public bool? GetSelfCheck(string moduleTitle)
    {
        EnsureLoaded();
        lock (_lock) return _state!.ModuleSelfChecks.TryGetValue(moduleTitle, out var v) ? v : null;
    }

    public void SetSelfCheck(string moduleTitle, bool canExplain)
    {
        EnsureLoaded();
        lock (_lock) { _state!.ModuleSelfChecks[moduleTitle] = canExplain; Save(); }
    }
}
