namespace SriAutomation.Automation;

public static class AuthenticationFailure
{
    // Never persist exception text: browser errors can contain redirect tokens or form values.
    public static string Message(string phase,Exception error)
    {
        var detail=phase switch {
            "Navigation"=>"No se pudo abrir el portal oficial. El fallo ocurrió antes de introducir las credenciales.",
            "LoginForm"=>"El portal se abrió, pero no se pudo completar el formulario de acceso. Evite reintentos repetidos y revise el navegador oficial.",
            _=>"No se pudo verificar la identidad después del acceso al portal."
        };
        var code=phase=="Navigation"?"SRI-NAV":phase=="LoginForm"?"SRI-FORM":"SRI-VERIFY";
        return $"{detail} No se declaró una sesión autenticada. Diagnóstico: {code}{(error is TimeoutException?"-TIMEOUT":"-BROWSER")}.";
    }
}
