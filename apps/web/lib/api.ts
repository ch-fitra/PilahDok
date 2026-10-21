import type {
  AnalysisResult,
  Capabilities,
  JobSecrets,
  JobStatus,
  SplitMethod,
  SplitSegment,
} from "./types";

const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:8080/api/v1";

export class ApiError extends Error {
  constructor(
    message: string,
    public readonly code: string,
    public readonly status: number,
  ) {
    super(message);
  }
}

async function expectJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const problem = (await response.json().catch(() => null)) as {
      title?: string;
      detail?: string;
    } | null;
    throw new ApiError(
      problem?.detail ?? "Permintaan tidak dapat diproses.",
      problem?.title ?? "request_failed",
      response.status,
    );
  }
  return (await response.json()) as T;
}

export async function getCapabilities(): Promise<Capabilities> {
  return expectJson(await fetch(`${API_URL}/capabilities`, { cache: "no-store" }));
}

export async function createJob(): Promise<JobSecrets> {
  const response = await expectJson<{
    jobId: string;
    accessToken: string;
    downloadToken: string;
    expiresAt: string;
  }>(await fetch(`${API_URL}/jobs`, { method: "POST", cache: "no-store" }));
  return response;
}

export function uploadDocument(
  job: JobSecrets,
  file: File,
  onProgress: (progress: number) => void,
  signal?: AbortSignal,
): Promise<JobStatus> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new ApiError("Upload dibatalkan.", "upload_cancelled", 499));
      return;
    }
    const request = new XMLHttpRequest();
    request.open("PUT", `${API_URL}/jobs/${job.jobId}/document`);
    request.setRequestHeader("X-Job-Token", job.accessToken);
    request.setRequestHeader("X-File-Name", encodeURIComponent(file.name));
    request.setRequestHeader(
      "Content-Type",
      "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    );
    request.upload.onprogress = (event) => {
      if (event.lengthComputable) onProgress(Math.round((event.loaded / event.total) * 100));
    };
    request.onerror = () => reject(new ApiError("Koneksi upload terputus.", "network_error", 0));
    request.timeout = 180_000;
    request.ontimeout = () =>
      reject(new ApiError("Upload melewati batas waktu.", "upload_timeout", 408));
    request.onabort = () => reject(new ApiError("Upload dibatalkan.", "upload_cancelled", 499));
    request.onload = () => {
      let payload: JobStatus & {
        detail?: string;
        title?: string;
      };
      try {
        payload = JSON.parse(request.responseText || "{}") as typeof payload;
      } catch {
        reject(
          new ApiError(
            "Server mengirim respons upload yang tidak valid.",
            "invalid_response",
            request.status,
          ),
        );
        return;
      }
      if (request.status >= 200 && request.status < 300) resolve(payload);
      else
        reject(
          new ApiError(
            payload.detail ?? "Upload gagal.",
            payload.title ?? "upload_failed",
            request.status,
          ),
        );
    };
    const abort = () => request.abort();
    signal?.addEventListener("abort", abort, { once: true });
    request.onloadend = () => signal?.removeEventListener("abort", abort);
    request.send(file);
  });
}

function accessHeaders(job: JobSecrets): HeadersInit {
  return { "X-Job-Token": job.accessToken };
}

export async function startAnalysis(job: JobSecrets): Promise<void> {
  await expectJson(
    await fetch(`${API_URL}/jobs/${job.jobId}/analyze`, {
      method: "POST",
      headers: accessHeaders(job),
    }),
  );
}

export async function getJob(job: JobSecrets, signal?: AbortSignal): Promise<JobStatus> {
  return expectJson(
    await fetch(`${API_URL}/jobs/${job.jobId}`, {
      headers: accessHeaders(job),
      cache: "no-store",
      signal,
    }),
  );
}

export async function getAnalysis(job: JobSecrets): Promise<AnalysisResult> {
  return expectJson(
    await fetch(`${API_URL}/jobs/${job.jobId}/analysis`, {
      headers: accessHeaders(job),
      cache: "no-store",
    }),
  );
}

export async function startSplit(
  job: JobSecrets,
  method: SplitMethod,
  segments: SplitSegment[],
): Promise<void> {
  await expectJson(
    await fetch(`${API_URL}/jobs/${job.jobId}/split`, {
      method: "POST",
      headers: { ...accessHeaders(job), "Content-Type": "application/json" },
      body: JSON.stringify({ method, segments }),
    }),
  );
}

export async function downloadResult(job: JobSecrets): Promise<void> {
  const response = await fetch(`${API_URL}/jobs/${job.jobId}/download`, {
    headers: { "X-Download-Token": job.downloadToken },
    cache: "no-store",
  });
  if (!response.ok) {
    await expectJson(response);
    return;
  }
  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = "PilahDok-hasil.zip";
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}

export async function deleteJob(job: JobSecrets): Promise<void> {
  const response = await fetch(`${API_URL}/jobs/${job.jobId}`, {
    method: "DELETE",
    headers: accessHeaders(job),
  });
  if (!response.ok) await expectJson(response);
}

export async function waitForStatus(
  job: JobSecrets,
  completedStatuses: string[],
  onUpdate: (status: JobStatus) => void,
  signal?: AbortSignal,
): Promise<JobStatus> {
  const deadline = Date.now() + 5 * 60 * 1000;
  while (Date.now() < deadline) {
    signal?.throwIfAborted();
    const status = await getJob(job, signal);
    onUpdate(status);
    if (status.status === "failed") {
      throw new ApiError(
        status.publicErrorMessage ?? "Pemrosesan gagal.",
        status.publicErrorCode ?? "processing_failed",
        422,
      );
    }
    if (completedStatuses.includes(status.status)) return status;
    await new Promise<void>((resolve, reject) => {
      const abort = () => {
        window.clearTimeout(timer);
        reject(signal?.reason);
      };
      const timer = window.setTimeout(() => {
        signal?.removeEventListener("abort", abort);
        resolve();
      }, 2_000);
      signal?.addEventListener("abort", abort, { once: true });
    });
  }
  throw new ApiError("Pemrosesan melewati batas waktu tunggu.", "client_timeout", 408);
}
