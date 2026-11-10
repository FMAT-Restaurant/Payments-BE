#!/usr/bin/env bash
set -euo pipefail

# Publica un evento DinningSessionReadyEvent real en RabbitMQ para probar el subscriber (PMC-5)
# y poblar una cuenta pendiente en Redis lista para cobrar en Postman.

RABBITMQ_URL="${RABBITMQ_URL:-http://localhost:15672}"
SESSION_ID="${1:-$(uuidgen | tr '[:upper:]' '[:lower:]')}"
EVENT_ID="$(uuidgen | tr '[:upper:]' '[:lower:]')"
MSG_ID="$(uuidgen | tr '[:upper:]' '[:lower:]')"
TABLE_ID="$(uuidgen | tr '[:upper:]' '[:lower:]')"
WAITER_ID="$(uuidgen | tr '[:upper:]' '[:lower:]')"
DISH1_ID="$(uuidgen | tr '[:upper:]' '[:lower:]')"
DISH2_ID="$(uuidgen | tr '[:upper:]' '[:lower:]')"
NOW_UTC="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
ORDER_NUM="$((RANDOM % 900 + 100))"

INNER_JSON=$(cat <<EOF
{"messageId":"${MSG_ID}","conversationId":"${MSG_ID}","sourceAddress":"rabbitmq://localhost/ordenes","destinationAddress":"rabbitmq://localhost/DinningSessionReady","messageType":["urn:message:DinningSessionReadyEvent"],"message":{"eventId":"${EVENT_ID}","dinningSessionId":"${SESSION_ID}","mesaId":"${TABLE_ID}","meseroId":"${WAITER_ID}","timestamp":"${NOW_UTC}","tableName":"Mesa ${ORDER_NUM}","waiterName":"Carlos Pérez","orderFolio":"ORD-${ORDER_NUM}","items":[{"platilloId":"${DISH1_ID}","nombre":"Tacos al Pastor","cantidad":2,"precioUnitario":85.00,"importe":170.00,"notas":"Sin piña"},{"platilloId":"${DISH2_ID}","nombre":"Agua de Horchata","cantidad":2,"precioUnitario":35.00,"importe":70.00,"notas":null}],"totales":{"subtotal":206.90,"impuestos":33.10,"totalEsperado":240.00}}}
EOF
)

PAYLOAD=$(python3 -c 'import json,sys; print(json.dumps({"properties":{"content_type":"application/vnd.masstransit+json"},"routing_key":"","payload":sys.stdin.read().strip(),"payload_encoding":"string"}))' <<< "$INNER_JSON")

RESPONSE=$(curl -s -u guest:guest -X POST "${RABBITMQ_URL}/api/exchanges/%2F/DinningSessionReady/publish" \
  -H "Content-Type: application/json" \
  -d "$PAYLOAD")

echo "RabbitMQ publish response: ${RESPONSE}"
echo "DinningSessionId generado: ${SESSION_ID}"
echo "Total esperado: 240.00 (Subtotal 206.90 + IVA 33.10)"
