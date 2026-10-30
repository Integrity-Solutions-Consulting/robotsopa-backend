using SriAutomation.Domain;
namespace SriAutomation.Infrastructure;
public interface IPanelDatabase
{
    void Initialize(string username, string password);
    (string User, string Role, bool MustChange)? Authenticate(string username, string password);
    object ListUsers();
    void SaveUser(string username, string role, string password);
    void ChangePassword(string username, string currentPassword, string newPassword);
    void SetPassword(string username, string password, bool mustChange);
    void SyncAccounts(IEnumerable<Account> accounts);
    void SyncAudit(IEnumerable<AuditEvent> events);
    HashSet<string> AssignedAccounts(string user);
    void Assign(string username, string account, bool assigned);
    PanelSettings Preferences(string username);
    void SavePreferences(string username, PanelSettings settings);
}
