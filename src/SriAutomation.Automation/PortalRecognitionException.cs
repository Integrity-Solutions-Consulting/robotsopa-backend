namespace SriAutomation.Automation;

// Only fixed reason codes cross the process boundary; never raw Playwright errors or page text.
public sealed class PortalRecognitionException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
