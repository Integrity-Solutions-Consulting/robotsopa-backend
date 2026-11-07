# Validación local de comprobantes

La descarga del SRI no forma parte de esta prueba. No coloque XML reales en el repositorio ni los adjunte a incidencias públicas. Use una cuenta de pruebas aislada y el almacenamiento autorizado de esa instalación.

1. Ejecute `dotnet test tests/SriAutomation.Tests/SriAutomation.Tests.csproj` desde `back`. Revise también las pruebas omitidas y sus requisitos; las pruebas sintéticas no sustituyen las reales.
2. Antes de indexar documentos, confirme que la raíz configurada en `SRI_OUTPUT_PATH` sea el almacenamiento correcto de la API. El indexador solo puede leer archivos accesibles para ese proceso; no accede automáticamente al disco del Bot.
3. En una instalación de prueba, use el endpoint existente `POST /api/v1/documents/reindex/{account}` con una sesión autorizada. Registre únicamente las cantidades devueltas de indexados y errores. El índice conserva los XML encontrados en su ubicación original; no sobrescribe un registro con un hash XML distinto.
4. Genere el mes desde Reportes y descargue el Excel. La generación valida un archivo temporal y conserva la versión anterior en la carpeta `.versiones` antes de sustituirla. No cambie ni elimine los XML originales.
5. Compare localmente el XML autorizado y el Excel: fecha de emisión, hora y fecha de autorización, emisor, receptor, números como texto, importe y cada línea de impuesto o retención. Para guías, contraste cada destinatario y producto; para notas, contraste documentos relacionados y motivos.
6. Para retenciones, contraste bases, tasas y valores individualmente. En `Resumen retenciones`, los subtotales usan filas visibles; las agrupaciones por tasa usan todas las filas. La columna de total del comprobante aparece una sola vez para evitar su duplicación.
7. Repita con factura, retención (incluida ATS), nota de crédito, nota de débito, guía y liquidación de compra, y con versiones disponibles. Anote tipo, versión, cantidades y campos que no coincidan, sin copiar identificaciones, claves ni datos tributarios privados a informes públicos.

Para una comparación local adicional, desde `back` ejecute `dotnet run --project src/SriAutomation.Compare -- "ruta-al-xml" "ruta-al-reporte.xlsx"`. Devuelve 0 si no encuentra diferencias en los campos que comprueba, 1 si encuentra diferencias y 2 si no puede comparar. Muestra su cobertura y los campos no verificados. Enmascara cadenas sensibles; los importes y fechas se muestran. La herramienta no verifica todavía todas las columnas especializadas ni sustituye la revisión tributaria.

La funcionalidad sigue requiriendo validación de los seis tipos con documentos reales y pruebas integrales con los servicios y el agente. Las hojas XML conservan campos de origen, pero no garantizan que todos tengan todavía una columna contable especializada. Revise los diagnósticos antes de usar los reportes para decisiones tributarias.
