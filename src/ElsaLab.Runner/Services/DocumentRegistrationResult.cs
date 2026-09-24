namespace ElsaLab.Runner.Services;

public sealed record DocumentRegistrationResult(
    bool IsValid,
    string ProcessingMessage,
    string GeneratedReference);
