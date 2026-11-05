namespace SriAutomation.Application;
public static class AccessMessages
{
    public static string? For(string? code)=>code switch {
        "SsoRequestRejected"=>"El SRI rechazó la transición de acceso con «Solicitud incorrecta». No se consultaron comprobantes. La ventana permanece disponible para revisar el portal.",
        "InvalidAccount"=>"El RUC configurado no tiene trece dígitos. Corrija la cuenta seleccionada.",
        "UnofficialOrigin"=>"La ventana todavía no muestra un documento del portal oficial del SRI.",
        "HumanChallenge"=>"El SRI solicita un CAPTCHA o código. Complételo en su ventana y pulse Listo.",
        "LoginRequired"=>"El formulario de acceso sigue visible. Termine de iniciar sesión en el SRI y pulse Listo.",
        "IdentityMismatch"=>"No se encontró el RUC de la cuenta seleccionada en la página. Compruebe que ingresó con la cuenta correcta.",
        "UnrecognizedSession"=>"La página no permite confirmar todavía el acceso. Abra el inicio o la página de su cuenta SRI y pulse Listo.",
        "Verified"=>"Acceso confirmado. Preparando la consulta en segundo plano.",
        "QueryControlsMissing"=>"El SRI no mostró los filtros o resultados esperados. La operación se detuvo sin declarar éxito.",
        "QueryAccountMissing"=>"La página de consulta no permitió confirmar la continuidad de la cuenta autorizada. No se enviaron filtros.",
        "QueryNavigationFailed"=>"El acceso se confirmó, pero el SRI no abrió la sección de emitidos. No se enviaron filtros. Puede mostrar la ventana para revisar la navegación.",
        "AutomaticLoginUnavailable"=>"No se reconoció un único formulario seguro de acceso. Complete el ingreso en la ventana oficial; no se reenvió la contraseña.",
        "CredentialUnavailable"=>"No se pudo abrir la credencial cifrada. Actualícela desde Configuración o complete el acceso en el SRI.",
        "LoginNotConfirmed"=>"El SRI sigue mostrando el acceso después del intento. Revise su mensaje en la ventana; la contraseña no se reenviará automáticamente.",
        "QuerySectionMissing"=>"No apareció la sección de consulta de emitidos: no hay filtros visibles. La ventana SRI se conserva para revisar la navegación.",
        "QueryTypeMissing"=>"La sección abierta no contiene un selector reconocible de tipos de comprobante. No se envió la consulta.",
        "QueryDateMissing"=>"No se reconocieron los filtros de fecha de emisión. No se envió la consulta.",
        "QuerySearchMissing"=>"No se encontró una única acción Consultar o Buscar. No se envió la consulta.",
        "DocumentProcessingFailed"=>"El comprobante llegó al sistema, pero no se pudo procesar o guardar. Revise el destino configurado, sus permisos y los registros de la API; si usa una carpeta sincronizada, compruebe también OneDrive.",
        "DocumentTransferFailed"=>"No se pudo confirmar la transferencia del comprobante al sistema local. Revise la conexión con la API y el destino configurado antes de reanudar.",
        "QueryTransportFailed"=>"La consulta o la transferencia no pudieron completarse. Revise el progreso; se conservaron los archivos válidos.",
        _=>null
    };
}
