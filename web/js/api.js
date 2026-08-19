const api = {
  token: localStorage.getItem("prefect.token") || "",

  async req(path, opts = {}) {
    const headers = Object.assign({ "Content-Type": "application/json" }, opts.headers || {});
    if (this.token) headers.Authorization = "Bearer " + this.token;
    const res = await fetch(path, Object.assign({}, opts, { headers }));
    if (res.status === 401) {
      this.token = "";
      localStorage.removeItem("prefect.token");
      throw new Error("Sign in again.");
    }
    if (!res.ok) {
      let msg = res.statusText;
      try {
        const j = await res.json();
        msg = j.detail || msg;
      } catch (_) {
        try { msg = await res.text(); } catch (__) {}
      }
      throw new Error(typeof msg === "string" ? msg : JSON.stringify(msg));
    }
    if (res.status === 204) return null;
    const ct = res.headers.get("content-type") || "";
    if (ct.includes("application/json")) return res.json();
    return res;
  },

  get(path) { return this.req(path); },
  post(path, body) { return this.req(path, { method: "POST", body: JSON.stringify(body) }); },

  async upload(file) {
    const fd = new FormData();
    fd.append("file", file);
    const headers = {};
    if (this.token) headers.Authorization = "Bearer " + this.token;
    const res = await fetch("/api/files", { method: "POST", body: fd, headers });
    if (!res.ok) throw new Error("Upload failed.");
    return res.json();
  },
};
