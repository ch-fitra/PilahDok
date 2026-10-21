import { afterEach, describe, expect, it, vi } from "vitest";
import { uploadDocument, waitForStatus } from "./api";

const job = { jobId: "a", accessToken: "test", downloadToken: "test", expiresAt: "" };
afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe("uploadDocument", () => {
  it("rejects cancellation before the upload request starts", async () => {
    const controller = new AbortController();
    controller.abort();
    await expect(
      uploadDocument(job, new File(["test"], "test.docx"), () => {}, controller.signal),
    ).rejects.toMatchObject({ code: "upload_cancelled" });
  });

  it("rejects an HTML proxy error instead of hanging on JSON parsing", async () => {
    class FailedRequest {
      responseText = "<html>Bad gateway</html>";
      status = 502;
      upload = {};
      onload?: () => void;
      onloadend?: () => void;
      open() {}
      setRequestHeader() {}
      send() {
        this.onload?.();
        this.onloadend?.();
      }
    }
    vi.stubGlobal("XMLHttpRequest", FailedRequest);
    await expect(
      uploadDocument(job, new File(["test"], "test.docx"), () => {}),
    ).rejects.toMatchObject({ code: "invalid_response", status: 502 });
  });
});

describe("waitForStatus", () => {
  it("cancels a pending poll without updating an abandoned screen", async () => {
    const controller = new AbortController();
    controller.abort();
    const update = vi.fn();
    await expect(waitForStatus(job, ["ready"], update, controller.signal)).rejects.toThrow();
    expect(update).not.toHaveBeenCalled();
  });
});
