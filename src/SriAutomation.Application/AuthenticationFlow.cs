using SriAutomation.Domain;

namespace SriAutomation.Application;

public static class AuthenticationFlow
{
    // Historical authentication records are retained in state, but only live worker
    // operations block credential changes.
    public static bool Active(AuthenticationOperation op) => op.Owner == "Worker" && op.Status is ("Ready" or "OpeningBrowser" or "WaitingForLogin" or "InspectingPortal");
}
