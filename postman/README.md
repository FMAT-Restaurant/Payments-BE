# Guía de Pruebas con Postman - Payments-BE

Esta carpeta contiene la colección de Postman y el entorno de variables para probar todos los endpoints del microservicio `Payments-BE` con casos de prueba automatizados (`pm.test`).

---

## 1. Requisitos Previos en tu Computadora

### A. Levantar la Infraestructura Local con Docker
Abre una terminal en la raíz del proyecto (`Payments-BE`) y ejecuta:

```bash
docker compose up -d
```

Esto levantará los 3 servicios necesarios configurados en `docker-compose.yml`:
- **SQL Server 2022**: Puerto `14330` (usuario `sa`, password `Payments_Dev_2026!`).
- **Redis 7**: Puerto `6379`.
- **RabbitMQ 4**: Puerto `5672` (broker) y `15672` (panel de gestión: http://localhost:15672).

Para verificar que los contenedores estén corriendo:
```bash
docker compose ps
```

---

### B. Aplicar Migraciones a la Base de Datos
La primera vez que levantes el contenedor de SQL Server, la base de datos `payments` estará vacía. Aplica las migraciones de EF Core ejecutando:

```bash
dotnet ef database update --project PaymentsModule
```
*(Si no tienes la herramienta instalada globalmente: `dotnet tool install --global dotnet-ef`)*

---

### C. Iniciar el Microservicio Payments-BE
Ejecuta el proyecto con el perfil de desarrollo HTTP:

```bash
dotnet run --project PaymentsModule
```

El servicio estará disponible en:
- API Base: `http://localhost:5150`
- Swagger UI: `http://localhost:5150/swagger`
- Health check: `http://localhost:5150/health`

---

## 2. Tokens JWT de Autenticación

El microservicio extrae la identidad del cajero/usuario exclusivamente desde los claims del JWT (`sub` / `NameIdentifier`):
- **Cajero:** Rol `Cajero` (o cualquier rol autenticado para cobro y apertura/cierre de turno).
- **Supervisor:** Rol `Administrador` o `Gerente` (requerido para listar turnos abiertos, arqueo de otros cajeros, cierre forzado y exportación CSV).

Si cuentas con el microservicio de autenticación / API Gateway, copia los tokens Bearer generados.

Para generar tokens JWT locales de prueba con la herramienta oficial de .NET:
```bash
# Token para Cajero
dotnet user-jwts create --project PaymentsModule --name "cajero-1" --role "Cajero"

# Token para Supervisor / Administrador
dotnet user-jwts create --project PaymentsModule --name "supervisor-1" --role "Administrador"
```
Copia el token generado y pégalo en Postman.

---

## 3. Importar y Usar en Postman

1. Abre **Postman**.
2. Haz clic en **Import** (esquina superior izquierda).
3. Selecciona los dos archivos de esta carpeta (si ya los habías importado antes, selecciona **Replace** para actualizar):
   - `postman/Payments-BE.postman_collection.json` (Colección)
   - `postman/Payments-BE.postman_environment.json` (Entorno)
4. En el selector de entornos de Postman (arriba a la derecha), selecciona **Payments-BE (Local Dev)**.
5. La autenticación ya viene configurada en la pestaña **Authorization** (`Bearer Token` usando `{{token}}` a nivel de colección y `{{supervisorToken}}` en la carpeta de supervisión y exportación), por lo que todas las peticiones heredan el token automáticamente (*Inherit auth from parent*). Si generas tokens nuevos, solo actualízalos en las variables del entorno.

---

## 4. Flujo de Pruebas Automatizadas Encadenadas

Las peticiones de la colección cuentan con scripts de prueba que guardan automáticamente los IDs generados:

1. **Abrir Turno:** Ejecuta `POST /cash-shifts/open` con fondo inicial. El test valida `201 Created` y guarda automáticamente `cashShiftId` en las variables.
2. **Consultar Turno y Arqueo:** Ejecuta `GET /cash-shifts/me` y `GET /cash-shifts/summary`.
3. **Cobro:** Ejecuta `POST /charge`. Si se procesa con éxito, el test valida `201 Created`, valida el cálculo del cambio y guarda el `folio` generado.
4. **Consultar Ticket e Historial:** Ejecuta `GET /payments/:folio` y `GET /payments/:folio/ticket`. Usarán automáticamente el `folio` guardado.
5. **Cerrar Turno:** Ejecuta `POST /cash-shifts/close` para finalizar el turno con el arqueo del cajero.
6. **Runner Completo:** Puedes hacer clic derecho en la colección y seleccionar **Run collection** para ejecutar la suite completa de pruebas en secuencia.
