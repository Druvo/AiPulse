namespace AiPulse.Services;

/// <summary>A 0-4 score plus a short label for the meter under a new-password field.</summary>
public sealed record PasswordScore(int Score, string Label);

/// <summary>
/// A small, dependency-free strength hint for the register and reset-password screens. It only guides; the real rule stays in
/// <see cref="UserService"/> (at least <see cref="MinLength"/> characters), so this never blocks a password the server would accept.
/// Length dominates (the strongest signal), character variety adds a little, and obvious patterns (one repeated character, a plain
/// keyboard/number run, a well-known password) are capped at "weak".
/// </summary>
public static class PasswordStrength
{
    /// <summary>Mirrors UserService's minimum password length.</summary>
    public const int MinLength = 6;

    private static readonly string[] Common =
    {
        "password", "123456", "1234567", "12345678", "123456789", "1234567890", "qwerty", "qwertyuiop", "abc123", "letmein", "welcome",
        "admin", "changeme", "iloveyou", "monkey", "dragon", "111111", "000000", "passw0rd", "password1", "aipulse"
    };

    private static readonly string[] Runs = { "0123456789", "abcdefghijklmnopqrstuvwxyz", "qwertyuiop", "asdfghjkl", "zxcvbnm" };

    public static PasswordScore Evaluate(string? password)
    {
        if (string.IsNullOrEmpty(password)) return new(0, "");
        if (password.Length < MinLength) return new(1, $"Too short - use at least {MinLength} characters");

        var lower = password.ToLowerInvariant();
        if (Common.Contains(lower) || lower.Distinct().Count() <= 2 || IsRun(lower))
            return new(1, "Weak - too easy to guess");

        var classes = 0;
        if (password.Any(char.IsLower)) classes++;
        if (password.Any(char.IsUpper)) classes++;
        if (password.Any(char.IsDigit)) classes++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) classes++;

        var score = password.Length switch
        {
            >= 16 => 3,
            >= 12 => 2,
            >= 9 => 1,
            _ => 0
        } + (classes >= 3 ? 1 : 0) + (classes == 4 && password.Length >= 10 ? 1 : 0);

        score = Math.Clamp(score, 1, 4);
        return new(score, score switch
        {
            1 => "Weak - try a longer passphrase",
            2 => "Fair",
            3 => "Good",
            _ => "Strong"
        });
    }

    /// <summary>The whole password is a slice of a common ascending/descending sequence ("abcdef", "654321", "qwerty").</summary>
    private static bool IsRun(string lower) =>
        Runs.Any(run => run.Contains(lower) || new string(run.Reverse().ToArray()).Contains(lower));
}
