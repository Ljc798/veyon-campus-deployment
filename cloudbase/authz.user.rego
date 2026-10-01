package authz.user

default allow := false

allow if {
  input.cloudbase.resource_type == "functions"
  input.request.path in {
    "/health",
    "/v1/heartbeat",
    "/v1/heartbeat/teacher",
    "/v1/releases/latest",
    "/v1/deployment-packages",
    "/v1/deployment-packages/mine",
    "/v1/deployment-package-publishers/me",
    "/v1/deployment-package-publishers"
  }
}

allow if {
  input.cloudbase.resource_type == "functions"
  startswith(input.request.path, "/v1/deployment-packages/")
}

allow if {
  input.cloudbase.resource_type == "functions"
  regex.match("^/v1/releases/[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}/artifact$", input.request.path)
}
