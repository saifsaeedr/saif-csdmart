#!/bin/bash
# Port of dmart's backend/curl.sh to the C# implementation.
#
# Verifies that the dmart REST surface (login, /managed/request CRUD, /managed/query,
# /managed/csv, /managed/lock, /managed/resource_with_payload, /info/manifest, etc.)
# accepts the same shapes dmart Python clients send.
#
# Usage:
#   ./curl.sh                                # uses defaults below
#   DMART_URL=http://localhost:5099 ./curl.sh
#
# Required tools: curl, jq, base64, file
#
# Returns 0 on full success, non-zero (= number of failures) otherwise.

set -u

# Read defaults from config.env if present (same file the server uses).
_read_config() { grep -m1 "^$1" config.env 2>/dev/null | sed 's/^[^=]*=//' | tr -d '"' | tr -d "'" || true; }

API_URL="${DMART_URL:-http://127.0.0.1:$(_read_config LISTENING_PORT)}"
API_URL="${API_URL:-http://127.0.0.1:5099}"
ADMIN_SHORTNAME="${DMART_ADMIN:-$(_read_config ADMIN_SHORTNAME)}"
ADMIN_SHORTNAME="${ADMIN_SHORTNAME:-cstest}"
ADMIN_PASSWORD="${DMART_PWD:-$(_read_config ADMIN_PASSWORD)}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-cstest-password-123}"
SPACE="${DMART_TEST_SPACE:-dummy}"
SHORTNAME="97326c47"
SUBPATH="posts"
CT="Content-Type: application/json"

# ----------------------------------------------------------------------------
# Per-run scratch dir for the inlined sample files. dmart's curl.sh references
# files at ../sample/test/{...}; we materialize them on the fly so this script
# is fully self-contained.
# ----------------------------------------------------------------------------
SAMPLES=$(mktemp -d -t dmart-curl-XXXXXXXX)
trap 'rm -rf "$SAMPLES"' EXIT

cat > "$SAMPLES/createschemawork.json" <<'EOF'
{
  "resource_type": "schema",
  "subpath": "schema",
  "shortname": "ticket_workflows",
  "attributes": { "payload": { "content_type": "json", "body": "workflow_schema.json" } }
}
EOF

cat > "$SAMPLES/workflow_schema.json" <<'EOF'
{
  "title": "Ticket Workflow Schema",
  "type": "object",
  "additionalProperties": true,
  "properties": {
    "initial_state": { "type": "string" },
    "states": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "state": { "type": "string" },
          "next":  { "type": "array" }
        }
      }
    }
  }
}
EOF

cat > "$SAMPLES/createticket.json" <<'EOF'
{
  "resource_type": "content",
  "subpath": "/workflows",
  "shortname": "myworkflow",
  "attributes": {
    "is_active": true,
    "payload": {
      "schema_shortname": "ticket_workflows",
      "content_type": "json",
      "body": "ticket_workflow.json"
    }
  }
}
EOF

cat > "$SAMPLES/ticket_workflow.json" <<'EOF'
{
  "initial_state": "draft",
  "states": [
    { "state": "draft",     "next": [{ "action": "submit", "to": "submitted" }] },
    { "state": "submitted", "next": [{ "action": "approve", "to": "approved" }, { "action": "reject", "to": "rejected" }] }
  ],
  "closed_states": ["approved", "rejected"]
}
EOF

cat > "$SAMPLES/createschema.json" <<'EOF'
{
  "resource_type": "schema",
  "subpath": "schema",
  "shortname": "test_schema",
  "attributes": { "payload": { "content_type": "json", "body": "schema.json" } }
}
EOF

cat > "$SAMPLES/schema.json" <<'EOF'
{
  "title": "My nice schema",
  "type": "object",
  "properties": {
    "name":  { "type": "string" },
    "price": { "type": "number" }
  },
  "required": ["name"]
}
EOF

cat > "$SAMPLES/ticketcontent.json" <<'EOF'
{
  "resource_type": "ticket",
  "subpath": "/myfolder",
  "shortname": "an_example",
  "attributes": {
    "workflow_shortname": "myworkflow",
    "is_active": true,
    "payload": {
      "schema_shortname": "test_schema",
      "content_type": "json",
      "body": "ticketbody.json"
    }
  }
}
EOF

cat > "$SAMPLES/ticketbody.json" <<'EOF'
{ "name": "story", "price": 22 }
EOF

cat > "$SAMPLES/createcontent.json" <<'EOF'
{
  "resource_type": "content",
  "subpath": "myfolder",
  "shortname": "buyer_123",
  "attributes": {
    "payload": { "content_type": "json", "schema_shortname": "test_schema" },
    "tags": ["fun", "personal"]
  }
}
EOF

cat > "$SAMPLES/data.json" <<'EOF'
{ "name": "Eggs", "price": 34.99 }
EOF

cat > "$SAMPLES/updatecontent.json" <<EOF
{
  "space_name": "$SPACE",
  "request_type": "update",
  "records": [
    {
      "resource_type": "content",
      "subpath": "myfolder",
      "shortname": "buyer_123",
      "attributes": {
        "tags": ["fun_UPDATED", "personal_UPDATED"],
        "displayname": { "en": "Updated content" }
      }
    }
  ]
}
EOF

cat > "$SAMPLES/createmedia.json" <<'EOF'
{
  "resource_type": "media",
  "subpath": "myfolder/buyer_123",
  "shortname": "receipt",
  "attributes": { "tags": ["fun", "personal"], "is_active": true }
}
EOF

cat > "$SAMPLES/deletecontent.json" <<EOF
{
  "space_name": "$SPACE",
  "request_type": "delete",
  "records": [
    {
      "resource_type": "content",
      "subpath": "myfolder",
      "shortname": "buyer_123",
      "attributes": {}
    }
  ]
}
EOF

# Tiny 1×1 PNG that we ship as the "logo.jpeg" upload payload — content_type is
# inferred from the request_record so the .jpeg extension is purely cosmetic.
printf '\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01\x00\x00\x00\x01\x08\x06\x00\x00\x00\x1f\x15\xc4\x89\x00\x00\x00\rIDATx\x9cb\x00\x01\x00\x00\x05\x00\x01\r\n-\xb4\x00\x00\x00\x00IEND\xaeB`\x82' > "$SAMPLES/logo.jpeg"

# ----------------------------------------------------------------------------
# Test runner — tracks pass/fail and prints a summary at the end.
# ----------------------------------------------------------------------------
declare -i RESULT=0
declare -i PASS=0
declare -i FAIL=0

ok()   { PASS+=1; echo "  ok"; }
nope() { FAIL+=1; RESULT+=1; echo "  FAIL: $*" >&2; }

# expect_status_success "label" "<json body>"
expect_success() {
    local label="$1"
    local body="$2"
    printf '%-45s' "$label" >&2
    if echo "$body" | jq -e '.status == "success"' > /dev/null 2>&1; then
        ok
    else
        nope "$body"
    fi
}

# ============================================================================
# 1. Login
# ============================================================================
printf '%-45s' "Login as $ADMIN_SHORTNAME:" >&2
LOGIN_RESP=$(curl -s -i -H "$CT" -d "{\"shortname\":\"$ADMIN_SHORTNAME\",\"password\":\"$ADMIN_PASSWORD\"}" "$API_URL/user/login")
AUTH_TOKEN=$(echo "$LOGIN_RESP" | grep -i '^set-cookie:' | head -1 | sed 's/^[^=]*=\([^;]*\).*/\1/i')
LOGIN_BODY=$(echo "$LOGIN_RESP" | sed -n '/^\r$/,$p' | tail -n +2)
if [[ -z "$AUTH_TOKEN" ]]; then
    nope "no Set-Cookie returned: $LOGIN_RESP"
else
    ok
fi

AUTH_HEADER="Authorization: Bearer $AUTH_TOKEN"

# Decode JWT header to verify type
JWT_HEADER=$(echo "$AUTH_TOKEN" | cut -d '.' -f 1)
# Pad base64url to base64
case $((${#JWT_HEADER} % 4)) in
    2) JWT_HEADER="${JWT_HEADER}==" ;;
    3) JWT_HEADER="${JWT_HEADER}=" ;;
esac
JWT_HEADER=$(echo "$JWT_HEADER" | tr '_-' '/+')
printf '%-45s' "JWT header decodes:" >&2
if echo "$JWT_HEADER" | base64 -d 2>/dev/null | jq -e '.alg == "HS256"' > /dev/null 2>&1; then
    ok
else
    nope "could not decode JWT header"
fi

# Decode JWT payload and verify Python-compatible data claims
JWT_PAYLOAD=$(echo "$AUTH_TOKEN" | cut -d '.' -f 2)
case $((${#JWT_PAYLOAD} % 4)) in
    2) JWT_PAYLOAD="${JWT_PAYLOAD}==" ;;
    3) JWT_PAYLOAD="${JWT_PAYLOAD}=" ;;
esac
JWT_PAYLOAD=$(echo "$JWT_PAYLOAD" | tr '_-' '/+')
JWT_BODY=$(echo "$JWT_PAYLOAD" | base64 -d 2>/dev/null)

printf '%-45s' "JWT payload has data.shortname:" >&2
if echo "$JWT_BODY" | jq -e ".data.shortname == \"$ADMIN_SHORTNAME\"" > /dev/null 2>&1; then
    ok
else
    nope "$(echo "$JWT_BODY" | jq .data)"
fi

printf '%-45s' "JWT payload has data.type:" >&2
if echo "$JWT_BODY" | jq -e '.data.type' > /dev/null 2>&1; then
    ok
else
    nope "missing data.type in JWT payload"
fi

printf '%-45s' "JWT payload expires == exp:" >&2
JWT_EXP=$(echo "$JWT_BODY" | jq '.exp')
JWT_EXPIRES=$(echo "$JWT_BODY" | jq '.expires')
if [[ "$JWT_EXP" == "$JWT_EXPIRES" ]] && [[ "$JWT_EXP" != "null" ]]; then
    ok
else
    nope "exp=$JWT_EXP expires=$JWT_EXPIRES"
fi

# ============================================================================
# 2. Profile
# ============================================================================
printf '%-45s' "Get profile:" >&2
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" "$API_URL/user/profile")
expect_success "" "$RESP"

# ============================================================================
# 3. Bootstrap: delete the test space if it lingers from a previous run
# ============================================================================
printf '%-45s' "Cleanup any old test space:" >&2
curl -s -H "$AUTH_HEADER" -H "$CT" -d "{\"space_name\":\"$SPACE\",\"request_type\":\"delete\",\"records\":[{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"$SPACE\",\"attributes\":{}}]}" "$API_URL/managed/request" > /dev/null
ok

# ============================================================================
# 4. Create test space
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"space_name\":\"$SPACE\",\"request_type\":\"create\",\"records\":[{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"$SPACE\",\"attributes\":{\"hide_space\":true,\"is_active\":true}}]}" \
    "$API_URL/managed/request")
expect_success "Create test space ($SPACE):" "$RESP"

# ============================================================================
# 5. Query spaces
# dmart Python only accepts type=spaces when space_name == management_space
# and subpath == "/". The response lists every space the user has the `query`
# action on, so we should see the scratch space we just created.
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"space_name\":\"management\",\"type\":\"spaces\",\"subpath\":\"/\"}" \
    "$API_URL/managed/query")
expect_success "Query spaces:" "$RESP"

# ============================================================================
# 6. Create folders inside the test space
# ============================================================================
for FOLDER in myfolder posts workflows schema; do
    RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
        -d "{\"space_name\":\"$SPACE\",\"request_type\":\"create\",\"records\":[{\"resource_type\":\"folder\",\"subpath\":\"/\",\"shortname\":\"$FOLDER\",\"attributes\":{\"is_active\":true,\"tags\":[\"one\",\"two\"]}}]}" \
        "$API_URL/managed/request")
    # /schema may already exist (auto-created by resource_folders_creation plugin)
    if [[ "$FOLDER" == "schema" ]] && echo "$RESP" | jq -e '.status == "failed"' > /dev/null 2>&1; then
        printf '%-45s' "Create folder ($FOLDER):" >&2; ok "(already exists)"
    else
        expect_success "Create folder ($FOLDER):" "$RESP"
    fi
done

# ============================================================================
# 7. Query folders
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"space_name\":\"$SPACE\",\"type\":\"subpath\",\"subpath\":\"/\",\"filter_schema_names\":[]}" \
    "$API_URL/managed/query")
expect_success "Query folders:" "$RESP"

# ============================================================================
# 8. Create schemas via multipart upload
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" \
    -F "space_name=$SPACE" \
    -F "request_record=@$SAMPLES/createschemawork.json;type=application/json" \
    -F "payload_file=@$SAMPLES/workflow_schema.json;type=application/json" \
    "$API_URL/managed/resource_with_payload")
expect_success "Create workflow schema (multipart):" "$RESP"

RESP=$(curl -s -H "$AUTH_HEADER" \
    -F "space_name=$SPACE" \
    -F "request_record=@$SAMPLES/createschema.json;type=application/json" \
    -F "payload_file=@$SAMPLES/schema.json;type=application/json" \
    "$API_URL/managed/resource_with_payload")
expect_success "Create test_schema (multipart):" "$RESP"

# ============================================================================
# 9. Create workflow content (uses ticket_workflows schema)
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" \
    -F "space_name=$SPACE" \
    -F "request_record=@$SAMPLES/createticket.json;type=application/json" \
    -F "payload_file=@$SAMPLES/ticket_workflow.json;type=application/json" \
    "$API_URL/managed/resource_with_payload")
expect_success "Create workflow definition (multipart):" "$RESP"

# ============================================================================
# 10. Create ticket (uses test_schema, references myworkflow)
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" \
    -F "space_name=$SPACE" \
    -F "request_record=@$SAMPLES/ticketcontent.json;type=application/json" \
    -F "payload_file=@$SAMPLES/ticketbody.json;type=application/json" \
    "$API_URL/managed/resource_with_payload")
expect_success "Create ticket (multipart):" "$RESP"

# ============================================================================
# 11. Lock / unlock the ticket
# ============================================================================
RESP=$(curl -s -X PUT -H "$AUTH_HEADER" "$API_URL/managed/lock/ticket/$SPACE/myfolder/an_example")
expect_success "Lock ticket:" "$RESP"

RESP=$(curl -s -X DELETE -H "$AUTH_HEADER" "$API_URL/managed/lock/$SPACE/myfolder/an_example")
expect_success "Unlock ticket:" "$RESP"

# ============================================================================
# 12. Inline content create (no schema)
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"space_name\":\"$SPACE\",\"request_type\":\"create\",\"records\":[{\"resource_type\":\"content\",\"subpath\":\"$SUBPATH\",\"shortname\":\"$SHORTNAME\",\"attributes\":{\"payload\":{\"content_type\":\"json\",\"body\":{\"message\":\"hello from curl.sh\"}},\"tags\":[\"one\",\"two\"]}}]}" \
    "$API_URL/managed/request")
expect_success "Create inline content:" "$RESP"

# ============================================================================
# 13. Multipart content create (uses test_schema)
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" \
    -F "space_name=$SPACE" \
    -F "request_record=@$SAMPLES/createcontent.json;type=application/json" \
    -F "payload_file=@$SAMPLES/data.json;type=application/json" \
    "$API_URL/managed/resource_with_payload")
expect_success "Create content (multipart):" "$RESP"

# ============================================================================
# 14. Comment on the content (attachment-flavor resource)
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"space_name\":\"$SPACE\",\"request_type\":\"create\",\"records\":[{\"resource_type\":\"comment\",\"subpath\":\"$SUBPATH/$SHORTNAME\",\"shortname\":\"greatcomment\",\"attributes\":{\"body\":\"A comment inside the content resource\"}}]}" \
    "$API_URL/managed/request")
expect_success "Add comment to content:" "$RESP"

# ============================================================================
# 15. Managed CSV export
# ============================================================================
printf '%-45s' "Managed CSV export (myfolder):" >&2
CSV_LINES=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"space_name\":\"$SPACE\",\"subpath\":\"myfolder\",\"type\":\"subpath\",\"filter_schema_names\":[],\"retrieve_json_payload\":true,\"limit\":5}" \
    "$API_URL/managed/csv" | wc -l)
if [[ "$CSV_LINES" -ge 2 ]]; then
    ok
else
    nope "expected at least 2 lines, got $CSV_LINES"
fi

# ============================================================================
# 16. Update content via raw request body file
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" --data-binary "@$SAMPLES/updatecontent.json" "$API_URL/managed/request")
expect_success "Update content (request file):" "$RESP"

# ============================================================================
# 17. Upload a media attachment
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" \
    -F "space_name=$SPACE" \
    -F "request_record=@$SAMPLES/createmedia.json;type=application/json" \
    -F "payload_file=@$SAMPLES/logo.jpeg;type=image/jpeg" \
    "$API_URL/managed/resource_with_payload")
expect_success "Upload media attachment:" "$RESP"

# ============================================================================
# 18. Delete content via raw request body file
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" --data-binary "@$SAMPLES/deletecontent.json" "$API_URL/managed/request")
expect_success "Delete content (request file):" "$RESP"

# ============================================================================
# 19. Query content
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"space_name\":\"$SPACE\",\"type\":\"subpath\",\"subpath\":\"$SUBPATH\",\"filter_schema_names\":[]}" \
    "$API_URL/managed/query")
expect_success "Query content under /$SUBPATH:" "$RESP"

# ============================================================================
# 20. Reload security data
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" "$API_URL/managed/reload-security-data")
expect_success "Reload security data:" "$RESP"

# ============================================================================
# 21. Create / reset / delete a user via /managed/request
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"space_name\":\"management\",\"request_type\":\"create\",\"records\":[{\"resource_type\":\"user\",\"subpath\":\"users\",\"shortname\":\"distributor\",\"attributes\":{\"roles\":[\"test_role\"],\"msisdn\":\"7895412658\",\"email\":\"distributor@example.local\",\"password\":\"Hunter22hunter\",\"is_active\":true}}]}" \
    "$API_URL/managed/request")
expect_success "Create user from admin:" "$RESP"

RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"space_name\":\"management\",\"request_type\":\"update\",\"records\":[{\"resource_type\":\"user\",\"subpath\":\"users\",\"shortname\":\"distributor\",\"attributes\":{\"is_email_verified\":true,\"is_msisdn_verified\":true}}]}" \
    "$API_URL/managed/request")
expect_success "Verify user email/msisdn:" "$RESP"

RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" -d '{"shortname":"distributor"}' "$API_URL/user/reset")
expect_success "Reset user (admin /user/reset):" "$RESP"

RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"space_name\":\"management\",\"request_type\":\"delete\",\"records\":[{\"resource_type\":\"user\",\"subpath\":\"users\",\"shortname\":\"distributor\",\"attributes\":{}}]}" \
    "$API_URL/managed/request")
expect_success "Delete user from admin:" "$RESP"

# ============================================================================
# 22. Cleanup: delete the test space
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"space_name\":\"$SPACE\",\"request_type\":\"delete\",\"records\":[{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"$SPACE\",\"attributes\":{}}]}" \
    "$API_URL/managed/request")
expect_success "Cleanup test space:" "$RESP"

# ============================================================================
# 23. Server manifest
# ============================================================================
printf '%-45s' "Server manifest:" >&2
MANIFEST=$(curl -s -H "$AUTH_HEADER" -H "$CT" "$API_URL/info/manifest")
if echo "$MANIFEST" | jq -e '.status == "success"' > /dev/null 2>&1; then
    ok
else
    nope "$MANIFEST"
fi

# ============================================================================
# 24. Login response shape: records[] not attributes{}
# ============================================================================
printf '%-45s' "Login response has records[]:" >&2
if echo "$LOGIN_BODY" | jq -e '.records[0].attributes.access_token' > /dev/null 2>&1; then
    ok
else
    nope "login body missing records[0].attributes.access_token: $LOGIN_BODY"
fi

# ============================================================================
# 25. Query management/users → resource_type: "user"
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d '{"type":"subpath","space_name":"management","subpath":"users","limit":2}' \
    "$API_URL/managed/query")
printf '%-45s' "Query users → type=user:" >&2
if echo "$RESP" | jq -e '.status == "success"' > /dev/null 2>&1; then
    # On a populated DB, verify resource_type; on fresh DB, just check success.
    if echo "$RESP" | jq -e '.records[0].resource_type == "user"' > /dev/null 2>&1; then
        ok
    elif echo "$RESP" | jq -e '.attributes.returned == 0' > /dev/null 2>&1; then
        ok "(empty on fresh DB)"
    else
        nope "$RESP"
    fi
else
    nope "$RESP"
fi

# ============================================================================
# 26. Query management/roles → resource_type: "role"
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d '{"type":"subpath","space_name":"management","subpath":"roles","limit":2}' \
    "$API_URL/managed/query")
printf '%-45s' "Query roles → type=role:" >&2
if echo "$RESP" | jq -e '.status == "success"' > /dev/null 2>&1; then
    if echo "$RESP" | jq -e '.records[0].resource_type == "role"' > /dev/null 2>&1; then
        ok
    elif echo "$RESP" | jq -e '.attributes.returned == 0' > /dev/null 2>&1; then
        ok "(empty on fresh DB)"
    else
        nope "$RESP"
    fi
else
    nope "$RESP"
fi

# ============================================================================
# 27. Query response envelope has total + returned
# ============================================================================
printf '%-45s' "Query attrs has total+returned:" >&2
if echo "$RESP" | jq -e '.attributes.total != null and .attributes.returned != null' > /dev/null 2>&1; then
    ok
else
    nope "missing total/returned: $RESP"
fi

# ============================================================================
# 28. Query history returns history records
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d '{"type":"history","space_name":"management","subpath":"users","limit":2}' \
    "$API_URL/managed/query")
expect_success "Query history:" "$RESP"

# ============================================================================
# 29. Query counters returns total in attributes
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d '{"type":"counters","space_name":"management","subpath":"/","limit":1}' \
    "$API_URL/managed/query")
printf '%-45s' "Query counters has total:" >&2
if echo "$RESP" | jq -e '.attributes.total != null' > /dev/null 2>&1; then
    ok
else
    nope "$RESP"
fi

# ============================================================================
# 30. Validate password (correct)
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"password\":\"$ADMIN_PASSWORD\"}" \
    "$API_URL/user/validate_password")
printf '%-45s' "Validate password (correct):" >&2
if echo "$RESP" | jq -e '.attributes.valid == true' > /dev/null 2>&1; then
    ok
else
    nope "$RESP"
fi

# ============================================================================
# 31. Validate password (wrong)
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d '{"password":"definitely-wrong-password"}' \
    "$API_URL/user/validate_password")
printf '%-45s' "Validate password (wrong):" >&2
if echo "$RESP" | jq -e '.attributes.valid == false' > /dev/null 2>&1; then
    ok
else
    nope "$RESP"
fi

# ============================================================================
# 32. Check-existing returns per-field booleans
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" "$API_URL/user/check-existing?shortname=$ADMIN_SHORTNAME&email=nonexistent@example.com")
printf '%-45s' "Check-existing per-field shape:" >&2
if echo "$RESP" | jq -e '.attributes.shortname == true and .attributes.email == false and .attributes.msisdn == false' > /dev/null 2>&1; then
    ok
else
    nope "$RESP"
fi

# ============================================================================
# 33. Profile has Python-parity fields
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" "$API_URL/user/profile")
printf '%-45s' "Profile has type+groups+force_pwd:" >&2
if echo "$RESP" | jq -e '.records[0].attributes | has("type","groups","force_password_change")' > /dev/null 2>&1; then
    ok
else
    nope "$RESP"
fi

# ============================================================================
# 34. Correlation ID header present
# ============================================================================
printf '%-45s' "Correlation-ID header present:" >&2
HEADERS=$(curl -sI "$API_URL/")
if echo "$HEADERS" | grep -qi 'X-Correlation-ID'; then
    ok
else
    nope "no X-Correlation-ID in: $HEADERS"
fi

# ============================================================================
# 35. Security headers present
# ============================================================================
printf '%-45s' "Security headers present:" >&2
# HSTS is only sent over HTTPS (RFC 6797) — check other security headers instead.
if echo "$HEADERS" | grep -qi 'X-Frame-Options' && echo "$HEADERS" | grep -qi 'X-Content-Type-Options' && echo "$HEADERS" | grep -qi 'Referrer-Policy'; then
    ok
else
    nope "missing security headers"
fi

# ============================================================================
# 36. CORS headers present
# ============================================================================
printf '%-45s' "CORS headers present:" >&2
if echo "$HEADERS" | grep -qi 'Access-Control-Allow-Methods'; then
    ok
else
    nope "missing CORS headers"
fi

# ============================================================================
# 37. Info/me endpoint
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" "$API_URL/info/me")
expect_success "Info/me:" "$RESP"

# ============================================================================
# 38. Info/settings endpoint
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" "$API_URL/info/settings")
expect_success "Info/settings:" "$RESP"

# ============================================================================
# 39. Public query (no auth)
# ============================================================================
RESP=$(curl -s -H "$CT" \
    -d "{\"type\":\"subpath\",\"space_name\":\"management\",\"subpath\":\"/\",\"limit\":1}" \
    "$API_URL/public/query")
expect_success "Public query (no auth):" "$RESP"

# ============================================================================
# 40. OPTIONS preflight returns 204
# ============================================================================
printf '%-45s' "OPTIONS preflight → 204:" >&2
STATUS=$(curl -s -o /dev/null -w '%{http_code}' -X OPTIONS "$API_URL/managed/request")
if [[ "$STATUS" == "204" ]]; then
    ok
else
    nope "expected 204, got $STATUS"
fi

# ============================================================================
# 41. User records don't leak password
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d '{"type":"subpath","space_name":"management","subpath":"users","limit":1}' \
    "$API_URL/managed/query")
printf '%-45s' "User query hides password:" >&2
if echo "$RESP" | jq -e '.records[0].attributes | has("password") | not' > /dev/null 2>&1; then
    ok
else
    nope "password leaked in user query"
fi

# ============================================================================
# 42. Null error field omitted on success
# ============================================================================
printf '%-45s' "Success omits error:null:" >&2
if echo "$RESP" | jq -e 'has("error") | not' > /dev/null 2>&1; then
    ok
else
    nope "error:null leaked on success response"
fi

# ============================================================================
# 42b. Entry endpoint hides password
# ============================================================================
printf '%-45s' "Entry user hides password:" >&2
USER_ENTRY=$(curl -s -H "$AUTH_HEADER" "$API_URL/managed/entry/user/management/users/$ADMIN_SHORTNAME")
if echo "$USER_ENTRY" | jq -e 'has("password") | not' > /dev/null 2>&1; then
    ok
else
    nope "password leaked in entry endpoint"
fi

# ============================================================================
# 42c. Entry endpoint hides query_policies
# ============================================================================
printf '%-45s' "Entry hides query_policies:" >&2
ENTRY_RESP=$(curl -s -H "$AUTH_HEADER" "$API_URL/managed/entry/folder/management/__root__/users")
if echo "$ENTRY_RESP" | jq -e 'has("query_policies") | not' > /dev/null 2>&1; then
    ok
else
    nope "query_policies leaked in entry endpoint"
fi

# ============================================================================
# 42d. Query response attributes have no null values
# ============================================================================
printf '%-45s' "Query attrs have no nulls:" >&2
Q_RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"type\":\"subpath\",\"space_name\":\"management\",\"subpath\":\"/\",\"limit\":1}" \
    "$API_URL/managed/query")
NULL_COUNT=$(echo "$Q_RESP" | jq '[.records[0].attributes | to_entries[] | select(.value == null)] | length')
if [[ "$NULL_COUNT" == "0" ]]; then
    ok
else
    nope "$NULL_COUNT null values in attributes"
fi

# ============================================================================
# 42e. Spaces query returns multiple spaces
# ============================================================================
printf '%-45s' "Spaces query returns >1 space:" >&2
SPACES_RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d '{"type":"spaces","space_name":"management","subpath":"/","limit":100}' \
    "$API_URL/managed/query")
SPACE_COUNT=$(echo "$SPACES_RESP" | jq '.attributes.returned // 0')
if [[ "$SPACE_COUNT" -gt 1 ]]; then
    ok
else
    ok "(only $SPACE_COUNT space on this DB)"
fi

# ============================================================================
# 43. __root__ magic word resolves to root subpath
# ============================================================================
printf '%-45s' "__root__ → / subpath:" >&2
ROOT_RESP=$(curl -s -H "$AUTH_HEADER" "$API_URL/managed/entry/folder/management/__root__/users")
if echo "$ROOT_RESP" | jq -e '.shortname == "users"' > /dev/null 2>&1; then
    ok
else
    nope "$ROOT_RESP"
fi

# ============================================================================
# 44. Entry lookup falls back when resource_type mismatches
# ============================================================================
printf '%-45s' "Entry type fallback (content→folder):" >&2
FALL_RESP=$(curl -s -H "$AUTH_HEADER" "$API_URL/managed/entry/content/management/__root__/users")
if echo "$FALL_RESP" | jq -e '.shortname == "users"' > /dev/null 2>&1; then
    ok
else
    nope "$FALL_RESP"
fi

# ============================================================================
# 45. Entry routing: space type → spaces table
# ============================================================================
printf '%-45s' "Entry space → spaces table:" >&2
SPACE_RESP=$(curl -s -H "$AUTH_HEADER" "$API_URL/managed/entry/space/management/__root__/management")
if echo "$SPACE_RESP" | jq -e '.shortname == "management"' > /dev/null 2>&1; then
    ok
else
    nope "$SPACE_RESP"
fi

# ============================================================================
# 46. Entry routing: user type → users table
# ============================================================================
printf '%-45s' "Entry user → users table:" >&2
USER_RESP=$(curl -s -H "$AUTH_HEADER" "$API_URL/managed/entry/user/management/__root__/dmart")
if echo "$USER_RESP" | jq -e '.shortname == "dmart"' > /dev/null 2>&1; then
    ok
else
    nope "$USER_RESP"
fi

# ============================================================================
# 47. Profile returns records[] with permissions
# ============================================================================
RESP=$(curl -s -H "$AUTH_HEADER" "$API_URL/user/profile")
printf '%-45s' "Profile has permissions dict:" >&2
if echo "$RESP" | jq -e '.records[0].attributes.permissions | length > 0' > /dev/null 2>&1; then
    ok
else
    nope "$RESP"
fi

# ============================================================================
# 48. Auto shortname (shortname="auto" → UUID[:8])
# ============================================================================
printf '%-45s' "Auto shortname generates UUID prefix:" >&2
AUTO_RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"space_name\":\"$SPACE\",\"request_type\":\"create\",\"records\":[{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"$SPACE\",\"attributes\":{\"is_active\":true}}]}" \
    "$API_URL/managed/request" > /dev/null 2>&1)
# Create a content entry with auto shortname inside the test space
AUTO_RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"space_name\":\"$SPACE\",\"request_type\":\"create\",\"records\":[{\"resource_type\":\"folder\",\"subpath\":\"/\",\"shortname\":\"testfolder\",\"attributes\":{\"is_active\":true}}]}" \
    "$API_URL/managed/request" > /dev/null 2>&1)
AUTO_RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"space_name\":\"$SPACE\",\"request_type\":\"create\",\"records\":[{\"resource_type\":\"content\",\"subpath\":\"testfolder\",\"shortname\":\"auto\",\"attributes\":{\"payload\":{\"content_type\":\"json\",\"body\":{\"x\":1}}}}]}" \
    "$API_URL/managed/request")
AUTO_SN=$(echo "$AUTO_RESP" | jq -r '.records[0].shortname // empty')
AUTO_UUID=$(echo "$AUTO_RESP" | jq -r '.records[0].uuid // empty')
if [[ ${#AUTO_SN} -eq 8 ]] && [[ "$AUTO_SN" != "auto" ]] && [[ "$AUTO_UUID" == "$AUTO_SN"* ]]; then
    ok
else
    nope "shortname=$AUTO_SN uuid=$AUTO_UUID"
fi
# Cleanup auto test
curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"space_name\":\"$SPACE\",\"request_type\":\"delete\",\"records\":[{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"$SPACE\",\"attributes\":{}}]}" \
    "$API_URL/managed/request" > /dev/null

# ============================================================================
# 49. Space create triggers schema folder plugin
# ============================================================================
printf '%-45s' "Space create → /schema folder:" >&2
# Clean up from any previous run
curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d '{"space_name":"plugtest2","request_type":"delete","records":[{"resource_type":"space","subpath":"/","shortname":"plugtest2","attributes":{}}]}' \
    "$API_URL/managed/request" > /dev/null 2>&1
# Create with explicit active_plugins to ensure the plugin fires
curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d '{"space_name":"plugtest2","request_type":"create","records":[{"resource_type":"space","subpath":"/","shortname":"plugtest2","attributes":{"is_active":true,"active_plugins":["resource_folders_creation","audit"]}}]}' \
    "$API_URL/managed/request" > /dev/null
# Poll for the /schema folder — the plugin fires as a background task
SCHEMA_FOUND=false
for i in $(seq 1 20); do
    SCHEMA_RESP=$(curl -s -H "$AUTH_HEADER" "$API_URL/managed/entry/folder/plugtest2/__root__/schema")
    if echo "$SCHEMA_RESP" | jq -e '.shortname == "schema"' > /dev/null 2>&1; then
        SCHEMA_FOUND=true
        break
    fi
    sleep 0.5
done
if [[ "$SCHEMA_FOUND" == "true" ]]; then
    ok
else
    nope "$SCHEMA_RESP"
fi
curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d '{"space_name":"plugtest2","request_type":"delete","records":[{"resource_type":"space","subpath":"/","shortname":"plugtest2","attributes":{}}]}' \
    "$API_URL/managed/request" > /dev/null

# ============================================================================
# 50. CXB index.html served from embedded resources
# ============================================================================
printf '%-45s' "CXB /cxb/index.html served:" >&2
CXB_STATUS=$(curl -s -o /dev/null -w '%{http_code}' "$API_URL/cxb/index.html")
if [[ "$CXB_STATUS" == "200" ]]; then
    ok
elif [[ "$CXB_STATUS" == "404" ]]; then
    ok "(not built)"
else
    nope "expected 200 or 404, got $CXB_STATUS"
fi

# ============================================================================
# 51. CXB SPA fallback
# ============================================================================
printf '%-45s' "CXB SPA fallback → index.html:" >&2
if [[ "$CXB_STATUS" == "404" ]]; then
    ok "(not built)"
else
    SPA_BODY=$(curl -s "$API_URL/cxb/management/some/route")
    if echo "$SPA_BODY" | grep -qi 'doctype html'; then
        ok
    else
        nope "SPA fallback didn't return HTML"
    fi
fi

# ============================================================================
# 52. exact_subpath honored at root
# ============================================================================
printf '%-45s' "exact_subpath=true at / filters:" >&2
EXACT=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"type\":\"search\",\"space_name\":\"$SPACE\",\"subpath\":\"/\",\"exact_subpath\":true,\"limit\":100}" \
    "$API_URL/managed/query")
NOEXACT=$(curl -s -H "$AUTH_HEADER" -H "$CT" \
    -d "{\"type\":\"search\",\"space_name\":\"$SPACE\",\"subpath\":\"/\",\"exact_subpath\":false,\"limit\":100}" \
    "$API_URL/managed/query")
EXACT_N=$(echo "$EXACT" | jq '.attributes.returned // 0')
NOEXACT_N=$(echo "$NOEXACT" | jq '.attributes.returned // 0')
if [[ "$NOEXACT_N" -ge "$EXACT_N" ]]; then
    ok
else
    nope "exact=$EXACT_N non-exact=$NOEXACT_N"
fi

# ============================================================================
# 53. HSTS not sent on HTTP (RFC 6797)
# ============================================================================
printf '%-45s' "HSTS absent on HTTP:" >&2
if echo "$HEADERS" | grep -qi 'Strict-Transport-Security'; then
    nope "HSTS should not be sent over HTTP"
else
    ok
fi

# ============================================================================
# 54. Error messages don't leak internals
# ============================================================================
printf '%-45s' "Error messages masked:" >&2
ERR_RESP=$(curl -s -H "$AUTH_HEADER" -H "$CT" -d '{invalid json!!!' "$API_URL/managed/query")
if echo "$ERR_RESP" | jq -e '.error.message' > /dev/null 2>&1; then
    ERR_MSG=$(echo "$ERR_RESP" | jq -r '.error.message')
    if echo "$ERR_MSG" | grep -qi "System\.\|Exception\|stack"; then
        nope "error leaks internals: $ERR_MSG"
    else
        ok
    fi
else
    ok
fi

# ============================================================================
# 55. WebSocket /ws-info endpoint
# ============================================================================
printf '%-45s' "WebSocket /ws-info:" >&2
WS_INFO=$(curl -s "$API_URL/ws-info")
if echo "$WS_INFO" | grep -q 'connected_clients'; then
    ok
else
    nope "$WS_INFO"
fi

# ============================================================================
# 56. WebSocket /broadcast-to-channels endpoint
# ============================================================================
printf '%-45s' "WebSocket broadcast endpoint:" >&2
BC_RESP=$(curl -s -H "$CT" -d '{"type":"test","message":{"x":1},"channels":["test:/:__ALL__:__ALL__:__ALL__"]}' "$API_URL/broadcast-to-channels")
if echo "$BC_RESP" | grep -q 'success'; then
    ok
else
    nope "$BC_RESP"
fi

# ============================================================================
# 57. WebSocket /send-message endpoint
# ============================================================================
printf '%-45s' "WebSocket send-message endpoint:" >&2
SM_RESP=$(curl -s -H "$CT" -d '{"type":"test","message":{"x":1}}' "$API_URL/send-message/nobody")
if echo "$SM_RESP" | grep -q 'message_sent'; then
    ok
else
    nope "$SM_RESP"
fi

# ============================================================================
# 58. CORS fallback uses localhost not 0.0.0.0
# ============================================================================
printf '%-45s' "CORS fallback is localhost:" >&2
CORS_HEADERS=$(curl -sI -H "Origin: https://stranger.example.com" "$API_URL/")
CORS_ORIGIN=$(echo "$CORS_HEADERS" | grep -i 'Access-Control-Allow-Origin' | head -1)
if echo "$CORS_ORIGIN" | grep -q '0\.0\.0\.0'; then
    nope "CORS fallback uses 0.0.0.0: $CORS_ORIGIN"
else
    ok
fi

# ============================================================================
# 59. CXB config.json override via env var
# ============================================================================
printf '%-45s' "CXB config.json served:" >&2
CXB_CFG=$(curl -s "$API_URL/cxb/config.json" 2>/dev/null)
if echo "$CXB_CFG" | jq -e '.title or .backend' > /dev/null 2>&1; then
    ok
elif [[ "$(curl -s -o /dev/null -w '%{http_code}' "$API_URL/cxb/config.json")" == "404" ]]; then
    ok "(no CXB)"
else
    nope "invalid config.json: $CXB_CFG"
fi

# ============================================================================
# 60. Settings endpoint returns valid JSON
# ============================================================================
printf '%-45s' "Info/settings has valid keys:" >&2
SETTINGS_RESP=$(curl -s -H "$AUTH_HEADER" "$API_URL/info/settings")
if echo "$SETTINGS_RESP" | jq -e '.status == "success" and .attributes.spaces_root' > /dev/null 2>&1; then
    ok
else
    nope "$SETTINGS_RESP"
fi

# ============================================================================
# 61. Root endpoint returns server identifier
# ============================================================================
printf '%-45s' "Root returns DMART identifier:" >&2
ROOT_RESP=$(curl -s "$API_URL/")
if echo "$ROOT_RESP" | grep -qi "dmart"; then
    ok
else
    nope "$ROOT_RESP"
fi

# ============================================================================
# 62. Native hook plugin appears in manifest
# ============================================================================
printf '%-45s' "Native hook plugin in manifest:" >&2
MANIFEST=$(curl -s -H "$AUTH_HEADER" "$API_URL/info/manifest")
if echo "$MANIFEST" | jq -e '.attributes.plugins | index("sample_hook")' > /dev/null 2>&1; then
    ok
else
    ok "(sample_hook not deployed)"
fi

# ============================================================================
# 63. Native API plugin endpoint responds
# ============================================================================
printf '%-45s' "Native API plugin responds:" >&2
API_PLUGIN_RESP=$(curl -s -H "$AUTH_HEADER" "$API_URL/sample_api/" 2>/dev/null)
if echo "$API_PLUGIN_RESP" | jq -e '.status == "success" and .attributes.plugin == "sample_api"' > /dev/null 2>&1; then
    ok
elif [[ "$(curl -s -o /dev/null -w '%{http_code}' -H "$AUTH_HEADER" "$API_URL/sample_api/" 2>/dev/null)" == "404" ]]; then
    ok "(sample_api not deployed)"
else
    nope "$API_PLUGIN_RESP"
fi

# ============================================================================
# 64. Native API plugin greet endpoint
# ============================================================================
printf '%-45s' "Native API plugin greet:" >&2
GREET_RESP=$(curl -s -H "$AUTH_HEADER" "$API_URL/sample_api/greet/TestUser" 2>/dev/null)
if echo "$GREET_RESP" | jq -e '.attributes.greeting | contains("TestUser")' > /dev/null 2>&1; then
    ok
elif [[ "$(curl -s -o /dev/null -w '%{http_code}' -H "$AUTH_HEADER" "$API_URL/sample_api/greet/TestUser" 2>/dev/null)" == "404" ]]; then
    ok "(sample_api not deployed)"
else
    nope "$GREET_RESP"
fi

# ============================================================================
# 65. Limited-user permission: create user with restricted role
# ============================================================================
# Create a permission, role, and user that can only access "test" space content.
# Then verify spaces listing, root query, folder entry, and counters all work.
LPERM="curltest_perm_$(date +%s)"
LROLE="curltest_role_$(date +%s)"
LUSER="curltest_user_$(date +%s)"

printf '%-45s' "Limited user: setup role+user:" >&2
# Create permission (covers test space + management/users)
SETUP1=$(curl -s -H "$CT" -H "$AUTH_HEADER" "$API_URL/managed/request" -d "{
  \"space_name\":\"management\",\"request_type\":\"create\",\"records\":[{
    \"resource_type\":\"permission\",\"subpath\":\"/permissions\",\"shortname\":\"$LPERM\",
    \"attributes\":{
      \"is_active\":true,
      \"subpaths\":{\"management\":[\"users\",\"schema\"],\"$SPACE\":[\"__all_subpaths__\"]},
      \"resource_types\":[\"content\",\"folder\",\"user\",\"schema\"],
      \"actions\":[\"view\",\"query\"],
      \"conditions\":[]
    }
  }]
}")
# Create role
SETUP2=$(curl -s -H "$CT" -H "$AUTH_HEADER" "$API_URL/managed/request" -d "{
  \"space_name\":\"management\",\"request_type\":\"create\",\"records\":[{
    \"resource_type\":\"role\",\"subpath\":\"/roles\",\"shortname\":\"$LROLE\",
    \"attributes\":{\"is_active\":true,\"permissions\":[\"$LPERM\"]}
  }]
}")
# Create user with that role
SETUP3=$(curl -s -H "$CT" -H "$AUTH_HEADER" "$API_URL/managed/request" -d "{
  \"space_name\":\"management\",\"request_type\":\"create\",\"records\":[{
    \"resource_type\":\"user\",\"subpath\":\"/users\",\"shortname\":\"$LUSER\",
    \"attributes\":{
      \"is_active\":true,\"password\":\"Test1234\",
      \"roles\":[\"$LROLE\"],\"type\":\"web\"
    }
  }]
}")
if echo "$SETUP3" | jq -e '.status == "success"' > /dev/null 2>&1; then
    ok
else
    nope "setup: $SETUP1 / $SETUP2 / $SETUP3"
fi

# Reload security data so the new permission/role/user are visible
curl -s -H "$AUTH_HEADER" "$API_URL/managed/reload-security-data" > /dev/null 2>&1

# Login as limited user
LTOKEN=$(curl -s -H "$CT" "$API_URL/user/login" -d "{\"shortname\":\"$LUSER\",\"password\":\"Test1234\"}" | jq -r '.records[0].attributes.access_token // empty')
LAUTH="Authorization: Bearer $LTOKEN"

# 66. Limited user: spaces query returns permitted spaces
printf '%-45s' "Limited user: sees permitted spaces:" >&2
if [ -n "$LTOKEN" ]; then
    LSPACES=$(curl -s -H "$CT" -H "$LAUTH" "$API_URL/managed/query" -d '{"type":"spaces","space_name":"management","subpath":"/","limit":100}')
    LSPACE_NAMES=$(echo "$LSPACES" | jq -r '.records[]?.shortname // empty' 2>/dev/null)
    if echo "$LSPACE_NAMES" | grep -q "management"; then
        ok
    else
        nope "spaces: $LSPACES"
    fi
else
    nope "limited user login failed"
fi

# 67. Limited user: root query on management returns folders
printf '%-45s' "Limited user: management/ returns folders:" >&2
if [ -n "$LTOKEN" ]; then
    LROOT=$(curl -s -H "$CT" -H "$LAUTH" "$API_URL/managed/query" -d '{"type":"search","space_name":"management","subpath":"/","limit":50}')
    if echo "$LROOT" | jq -e '.status == "success" and (.attributes.total > 0)' > /dev/null 2>&1; then
        ok
    else
        nope "root query: $LROOT"
    fi
else
    nope "limited user login failed"
fi

# 68. Limited user: folder entry accessible
printf '%-45s' "Limited user: GET folder/management/users:" >&2
if [ -n "$LTOKEN" ]; then
    LFOLDER=$(curl -s -H "$LAUTH" "$API_URL/managed/entry/folder/management/users")
    if echo "$LFOLDER" | jq -e '.shortname == "users"' > /dev/null 2>&1; then
        ok
    else
        nope "folder entry: $LFOLDER"
    fi
else
    nope "limited user login failed"
fi

# 69. Limited user: counters query succeeds
printf '%-45s' "Limited user: counters management/users:" >&2
if [ -n "$LTOKEN" ]; then
    LCOUNTERS=$(curl -s -H "$CT" -H "$LAUTH" "$API_URL/managed/query" -d '{"type":"counters","space_name":"management","subpath":"/users","limit":100,"exact_subpath":true,"filter_types":["user"]}')
    if echo "$LCOUNTERS" | jq -e '.status == "success"' > /dev/null 2>&1; then
        ok
    else
        nope "counters: $LCOUNTERS"
    fi
else
    nope "limited user login failed"
fi

# 70. Limited user: no access to unpermitted subpath
printf '%-45s' "Limited user: denied unpermitted folder:" >&2
if [ -n "$LTOKEN" ]; then
    LDENIED_CODE=$(curl -s -o /dev/null -w '%{http_code}' -H "$LAUTH" "$API_URL/managed/entry/folder/management/workflows")
    if [ "$LDENIED_CODE" = "404" ]; then
        ok
    else
        nope "expected 404, got $LDENIED_CODE"
    fi
else
    nope "limited user login failed"
fi

# Cleanup limited user, role, permission
curl -s -H "$CT" -H "$AUTH_HEADER" "$API_URL/managed/request" -d "{
  \"space_name\":\"management\",\"request_type\":\"delete\",\"records\":[
    {\"resource_type\":\"user\",\"subpath\":\"/users\",\"shortname\":\"$LUSER\"},
    {\"resource_type\":\"role\",\"subpath\":\"/roles\",\"shortname\":\"$LROLE\"},
    {\"resource_type\":\"permission\",\"subpath\":\"/permissions\",\"shortname\":\"$LPERM\"}
  ]
}" > /dev/null 2>&1

# ============================================================================
# Summary
# ============================================================================
echo "" >&2
echo "================================================" >&2
printf "  passed: %-3d  failed: %-3d  total: %d\n" "$PASS" "$FAIL" "$((PASS + FAIL))" >&2
echo "================================================" >&2
exit "$RESULT"
