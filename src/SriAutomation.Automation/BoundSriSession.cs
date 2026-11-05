using Microsoft.Playwright;

namespace SriAutomation.Automation;

// Proof belongs to a single, already verified browser context and account. It is never serialized.
public sealed class BoundSriSession
{
    private readonly IBrowserContext context;
    private readonly string taxId;
    private BoundSriSession(IBrowserContext context,string taxId){this.context=context;this.taxId=taxId;}
    public static async Task<BoundSriSession> Verify(IPage page,string taxId)
    {
        var check=await SriAccessEvidence.Inspect(page,taxId);
        if(!check.Verified)throw new PortalRecognitionException(check.Code);
        return new(page.Context,taxId);
    }
    public async Task<AccessCheck> Inspect(IPage page,string account)
    {
        if(!ReferenceEquals(context,page.Context)||account!=taxId)return new(false,"IdentityMismatch");
        var check=await SriAccessEvidence.Inspect(page,taxId);
        // Observed production application displays the exact RUC and its query form,
        // but has no logout control. This exception requires the original verified
        // context and account; it cannot establish a session on its own.
        if(check.Code=="UnrecognizedSession"&&new Uri(page.Url).AbsolutePath.StartsWith("/comprobantes-electronicos-internet/pages/consultas/",StringComparison.Ordinal)&&
            (await page.Locator("form[id=consultaDocumentoForm]:visible").CountAsync()==1||await ObservedPortalProfile.HasTypeControl(page)))return new(true,"Verified");
        if(check.Verified||check.Code!="IdentityMismatch")return check;
        if(new Uri(page.Url).AbsolutePath.TrimEnd('/').Equals("/sri-en-linea/contribuyente/perfil",StringComparison.OrdinalIgnoreCase))return check;
        // Some SRI applications omit the RUC in the page header. Require the same verified
        // context, official origin, no login/challenge and an actual visible logout control.
        return await SriAccessEvidence.HasLogout(page)?new(true,"Verified"):new(false,"QueryAccountMissing");
    }
}
