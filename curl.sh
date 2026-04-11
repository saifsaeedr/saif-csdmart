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

API_URL="${DMART_URL:-http://127.0.0.1:5099}"
ADMIN_SHORTNAME="${DMART_ADMIN:-cstest}"
ADMIN_PASSWORD="${DMART_PWD:-cstest-password-123}"
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
    expect_success "Create folder ($FOLDER):" "$RESP"
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
# Summary
# ============================================================================
echo "" >&2
echo "================================================" >&2
printf "  passed: %-3d  failed: %-3d  total: %d\n" "$PASS" "$FAIL" "$((PASS + FAIL))" >&2
echo "================================================" >&2
exit "$RESULT"
