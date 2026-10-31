export function localDockerEndpoint(endpoint) {
  if (typeof endpoint !== "string") return false;
  if (endpoint.startsWith("unix:///")) return true;
  if (/^npipe:\/\/\/\/\.\/pipe\/[a-z0-9_-]+$/i.test(endpoint)) return true;
  try {
    const url = new URL(endpoint);
    return (
      url.protocol === "tcp:" &&
      ["127.0.0.1", "[::1]"].includes(url.hostname) &&
      !url.username &&
      !url.password
    );
  } catch {
    return false;
  }
}
