import test from "node:test";
import assert from "node:assert/strict";
import { localDockerEndpoint } from "./scope.mjs";
test("acceptance accepts local Docker IPC and literal loopback only", () => {
  for (const endpoint of [
    "unix:///var/run/docker.sock",
    "npipe:////./pipe/dockerDesktopLinuxEngine",
    "tcp://127.0.0.1:2375",
    "tcp://[::1]:2375",
  ])
    assert(localDockerEndpoint(endpoint));
});
test("acceptance refuses remote Docker, SSH and arbitrary network targets without connecting", () => {
  for (const endpoint of [
    "tcp://192.0.2.10:2375",
    "ssh://example.invalid",
    "https://example.invalid",
    "tcp://0.0.0.0:2375",
    "tcp://user:password@127.0.0.1:2375",
  ])
    assert.equal(localDockerEndpoint(endpoint), false);
});
test("missing or malformed endpoint cannot silently pass the scope gate", () => {
  for (const endpoint of [
    "",
    undefined,
    null,
    123,
    "npipe://remote/pipe/docker",
    "not-an-endpoint",
  ])
    assert.equal(localDockerEndpoint(endpoint), false);
});
