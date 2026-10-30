#!/bin/sh
set -eu
echo 'Iniciando la preparacion de PostgreSQL...'
dotnet SriAutomation.Api.dll --db-init
echo 'INICIALIZACION_COMPLETADA: PostgreSQL verificado. Detenga esta aplicacion temporal en Dokploy.'
# Swarm treats a successful exit as a stopped service and restarts it. Keep the
# task visible until an operator stops the temporary application in Dokploy.
exec sleep infinity
