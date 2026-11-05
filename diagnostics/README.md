# Prueba aislada de red del Worker

Ejecute esta prueba en el contenedor Worker que presenta el problema, con su misma imagen, red y variables de entorno. No requiere desplegar esta versión del repositorio ni instalar paquetes. La sonda usa exclusivamente el Node y Playwright incluidos en `/app/.playwright`, y hace solicitudes públicas sin abrir sesión, cargar cookies ni enviar credenciales. No modifica PostgreSQL. El ensayo puede tardar varios minutos: cada navegación mantiene el límite existente de 45 segundos.

Desde una terminal del **host** con acceso al repositorio y a Docker:

```sh
docker exec -i NOMBRE_WORKER /app/.playwright/node/linux-x64/node - < back/diagnostics/sri-network-probe.js
```

Desde la terminal **del contenedor** en Dokploy, copie el contenido completo de `back/diagnostics/sri-network-probe.js` entre estas líneas y ejecútelas (no hace falta guardar el archivo ni reconstruir la imagen):

```sh
/app/.playwright/node/linux-x64/node - <<'SRI_PROBE'
PEGAR_AQUI_EL_CONTENIDO_COMPLETO_DE_sri-network-probe.js
SRI_PROBE
```

Guarde la salida JSON. La prueba compara HTTPS directo de Node, sin proxy y con validación TLS, por IPv4 y por IPv6, con cuatro navegaciones Chromium independientes: `baseline` reproduce la configuración de lanzamiento del Worker; `http1` deshabilita HTTP/2 y QUIC únicamente para la prueba; `ipv4` fija la primera dirección IPv4 obtenida por DNS; y `direct` deshabilita el proxy únicamente para la prueba. El cliente HTTPS de Node usa HTTP/1.1, por lo que no demuestra por sí solo que HTTP/2 sea el problema. En `baseline`, Playwright puede agregar sus propios argumentos predeterminados; la sonda usa el mismo `launch` predeterminado del Worker, salvo por el registro de red. Ninguna variante altera la validación TLS.

`dns` muestra direcciones; `direct_tls` y `direct_response` muestran IP, TLS, ALPN, HTTP y estado; los eventos `browser_request` y `browser_response` muestran redirecciones, protocolo, TLS e IP que Chromium informa. `urlHash` identifica la URL completa sin imprimir rutas, consultas ni tokens: compare `initialHash` con el hash final y con cada redirección. `about:blank` tras timeout no es una URL final del servidor. La existencia de variables de proxy no demuestra que Chromium las use, y los valores no se imprimen. Un fallo IPv6 puede indicar que no hay ruta IPv6, no que Chromium la haya elegido. `browser_failure` puede aparecer después de `browser_error` cuando Playwright cancela la petición; no atribuya ese aborto a la causa inicial.

Cada variante escribe su netlog en la ruta `/tmp/sri-chromium-...json` indicada en `browser_netlog`. Estos archivos pueden contener URL, direcciones y configuración de proxy; trátelos como sensibles, revíselos localmente y no los publique íntegros. El formato netlog de Chromium contiene `constants.logEventTypes` y eventos relacionados con `PROXY`, `HOST_RESOLVER`, `CONNECT`, `SSL`, `HTTP2` y `QUIC`; permiten investigar selección efectiva de proxy, resoluciones, intentos y errores incluso cuando no llega respuesta. Al terminar, elimine únicamente los archivos de esta prueba si ya no son necesarios. El protocolo efectivo solo se conoce si Chromium recibe respuesta; sin ella, el netlog permite investigar intentos, pero puede no demostrar una causa única.

Compare las variantes: si solo `http1` funciona, investigue negociación HTTP/2/QUIC; si solo `ipv4` funciona, investigue DNS/ruta IPv6; si solo `direct` funciona, investigue el proxy efectivo; si todas fallan, use los eventos de conexión/TLS del netlog y compare con HTTPS directo. Las diferencias son indicios, no pruebas concluyentes aisladas: repita en condiciones equivalentes y revise el netlog antes de cambiar la configuración de producción.

## Comparación controlada posterior al netlog

Para la evidencia de IPv4/TLS/HTTP/1.1 descrita, ejecute `sri-controlled-comparison.js` en el **mismo contenedor Worker** y en una ventana sin trabajos activos. No cambia archivos ni configuración del Worker, no usa credenciales ni sigue redirecciones en Node; Chromium se cierra tras recibir las cabeceras de la navegación inicial o alcanzar 45 segundos. La prueba hace doce solicitudes Node y dos navegaciones Chromium, secuenciales en dos rondas, alternando su orden. Deténgala ante un desafío, CAPTCHA, bloqueo o limitación; no intente resolverlos automáticamente. Una respuesta HTTP, incluyendo 403 o 429, no equivale a éxito de acceso.

```sh
docker exec -i NOMBRE_WORKER /app/.playwright/node/linux-x64/node - < back/diagnostics/sri-controlled-comparison.js
```

O pegue el contenido del archivo en un heredoc en la terminal del contenedor, como en el ejemplo anterior. Guarde la salida JSON y comparta únicamente esa salida tras revisarla: contiene IP y cabeceras públicas permitidas, pero no cookies, autorización, cuerpo, rutas de redirección ni valores de proxy. Los valores de cabeceras están limitados a una lista explícita; `browser_wire_headers_unavailable` significa que CDP no entregó evidencia utilizable y no debe interpretarse como ausencia de cabeceras.

Cada ronda compara Node por IPv4, TLS verificado y HTTP/1.1: configuración predeterminada, `Accept`, `Accept-Encoding` y `Accept-Language` por separado, y dos solicitudes con el mismo agente keep-alive. `reusedSocket: true` confirma reutilización; si es false, la segunda solicitud no demuestra el comportamiento de una conexión reutilizada. Se comparan las cabeceras efectivamente disponibles de Chromium con las de Node, sin cambiar la identidad del navegador ni simular cabeceras de seguridad. Las variantes Node no replican el conjunto completo, orden o serialización de cabeceras de Chromium. Chromium mantiene sus valores normales y puede usar una ruta/proxy, ALPN o dirección distinta: contraste los campos y el netlog existente antes de atribuir una diferencia. Las dos rondas ayudan a detectar variaciones temporales, pero no prueban causalidad por sí solas.

Si solo una variante de Node falla en ambas rondas mientras el control cercano responde, investigue esa cabecera y repita el contraste; si solo falla la segunda solicitud con `reusedSocket: true`, investigue la conexión persistente. Si Node responde siempre y Chromium no, incluso con la misma IP, protocolo y ruta confirmados, queda una diferencia asociada al cliente, no una causa específica: pueden intervenir identidad declarada, orden o formato HTTP, perfil TLS u otras políticas del servidor. Un netlog no revela la lógica del SRI. No sustituya User-Agent ni modifique señales de automatización para sortear controles; si se identifica una política de acceso, consulte al SRI. No se propone cambiar producción hasta obtener una causa reproducible y una corrección autorizada.
