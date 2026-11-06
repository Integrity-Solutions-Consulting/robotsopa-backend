namespace SriAutomation.Api;

public static class DeliveryRegistrationDiagnostics
{
    public static void Rejected(HttpContext context, ILogger logger, string cause)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || context.Request.Path != "/api/v1/delivery-agents") return;
        var message = cause switch
        {
            "HTTPS_NOT_RECOGNIZED" => "HTTPS no reconocido por la API",
            "ORIGIN_UNAUTHORIZED" => "Origen no autorizado",
            "SESSION_INVALID" => "Sesión inexistente, inválida o vencida",
            "ROLE_UNAUTHORIZED" => "Rol no autorizado",
            "PASSWORD_CHANGE_REQUIRED" => "Cambio de contraseña obligatorio",
            _ => throw new ArgumentOutOfRangeException(nameof(cause))
        };
        logger.LogWarning("Registro de agente rechazado. Causa: {Cause}. {Reason}.", cause, message);
    }
}
