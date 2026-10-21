"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import {
  ApiError,
  createJob,
  deleteJob,
  downloadResult,
  getAnalysis,
  getJob,
  getCapabilities,
  startAnalysis,
  startSplit,
  uploadDocument,
  waitForStatus,
} from "@/lib/api";
import { buildSegments, candidatesForMethod } from "@/lib/segments";
import { initialMethod, manualSelection } from "@/lib/editor";
import type {
  AnalysisResult,
  Capabilities,
  JobSecrets,
  JobStatus,
  SplitMethod,
  SplitSegment,
} from "@/lib/types";
import { SplitForm } from "@/components/SplitForm";
import { StructurePicker } from "@/components/StructurePicker";

type Phase =
  | "idle"
  | "uploading"
  | "analyzing"
  | "ready"
  | "processing"
  | "completed"
  | "deleted"
  | "error";

const methods: Array<{ value: SplitMethod; label: string; description: string }> = [
  { value: "heading1", label: "Per bab utama", description: "Mengikuti judul bab di dokumen" },
  { value: "heading2", label: "Per subbab", description: "Mengikuti judul subbagian" },
  {
    value: "chapter",
    label: "Per judul BAB",
    description: "Mengikuti tulisan BAB I, BAB 1, atau CHAPTER",
  },
  {
    value: "section",
    label: "Per bagian Word",
    description: "Mengikuti pembagian bagian yang tersimpan",
  },
  {
    value: "pageBreak",
    label: "Per pemisah halaman",
    description: "Mengikuti tanda pemisah yang disisipkan di Word, bukan setiap halaman visual",
  },
  { value: "manual", label: "Pilih sendiri", description: "Klik lokasi potong pada isi dokumen" },
];

export default function HomePage() {
  const [phase, setPhase] = useState<Phase>("idle");
  const [capabilities, setCapabilities] = useState<Capabilities | null>(null);
  const [job, setJob] = useState<JobSecrets | null>(null);
  const [jobStatus, setJobStatus] = useState<JobStatus | null>(null);
  const [analysis, setAnalysis] = useState<AnalysisResult | null>(null);
  const [method, setMethod] = useState<SplitMethod>("heading1");
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [uploadProgress, setUploadProgress] = useState(0);
  const [dragActive, setDragActive] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const errorRef = useRef<HTMLDivElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const uploadAbortRef = useRef<AbortController | null>(null);
  const operationVersion = useRef(0);

  useEffect(() => {
    getCapabilities()
      .then(setCapabilities)
      .catch(() => undefined);
  }, []);

  useEffect(() => {
    if (error) errorRef.current?.focus();
  }, [error]);

  useEffect(() => {
    if (phase !== "ready") return;

    window.history.replaceState(null, "", window.location.pathname);
    window.scrollTo({ top: 0, behavior: "smooth" });
  }, [phase]);

  const methodCandidates = useMemo(
    () => (analysis ? candidatesForMethod(analysis.splitCandidates, method) : []),
    [analysis, method],
  );

  const segments = useMemo(
    () => buildSegments(methodCandidates, selectedIds),
    [methodCandidates, selectedIds],
  );

  async function begin(file: File) {
    if (
      !file.name.toLowerCase().endsWith(".docx") ||
      file.size === 0 ||
      file.size > (capabilities?.maxUploadBytes ?? 52_428_800)
    ) {
      showError(
        new ApiError(
          "Pilih file .docx yang tidak kosong dan sesuai batas ukuran.",
          "invalid_file",
          422,
        ),
      );
      return;
    }
    setError(null);
    setAnalysis(null);
    setUploadProgress(0);
    setPhase("uploading");
    setMethod("heading1");
    const version = ++operationVersion.current;
    const abortController = new AbortController();
    uploadAbortRef.current = abortController;
    try {
      const created = await createJob();
      setJob(created);
      const uploaded = await uploadDocument(
        created,
        file,
        setUploadProgress,
        abortController.signal,
      );
      setJobStatus(uploaded);
      setPhase("analyzing");
      await startAnalysis(created);
      await waitForStatus(created, ["ready"], setJobStatus, abortController.signal);
      const result = await getAnalysis(created);
      setAnalysis(result);
      const recommended = initialMethod(result.splitCandidates);
      setMethod(recommended);
      setSelectedIds(
        new Set(
          recommended === "manual"
            ? []
            : candidatesForMethod(result.splitCandidates, recommended).map(
                (candidate) => candidate.id,
              ),
        ),
      );
      setPhase("ready");
    } catch (caught) {
      if (version === operationVersion.current) showError(caught);
    } finally {
      uploadAbortRef.current = null;
    }
  }

  async function processDocument(submittedSegments: SplitSegment[]) {
    if (!job) return;
    setError(null);
    setPhase("processing");
    const version = ++operationVersion.current;
    const controller = new AbortController();
    uploadAbortRef.current = controller;
    try {
      await startSplit(job, method, submittedSegments);
      const completed = await waitForStatus(job, ["completed"], setJobStatus, controller.signal);
      setJobStatus(completed);
      setPhase("completed");
    } catch (caught) {
      if (version === operationVersion.current) showError(caught);
    } finally {
      if (version === operationVersion.current) uploadAbortRef.current = null;
    }
  }

  async function download() {
    if (!job) return;
    setError(null);
    try {
      await downloadResult(job);
      setJobStatus(await getJob(job));
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : "Unduhan gagal. Silakan coba lagi.");
    }
  }

  async function removeJob() {
    if (!job) return;
    ++operationVersion.current;
    uploadAbortRef.current?.abort();
    setError(null);
    try {
      await deleteJob(job);
      setJob(null);
      setJobStatus(null);
      setAnalysis(null);
      setPhase("deleted");
    } catch (caught) {
      showError(caught);
    }
  }

  function showError(caught: unknown) {
    const message =
      caught instanceof ApiError ? caught.message : "Terjadi kesalahan. Silakan coba lagi.";
    setError(message);
    setPhase("error");
  }

  async function reset() {
    if (phase === "uploading" || phase === "analyzing" || phase === "processing") return;
    if (job) {
      try {
        await deleteJob(job);
      } catch (caught) {
        if (!(caught instanceof ApiError && (caught.status === 404 || caught.status === 410))) {
          showError(caught);
          return;
        }
      }
    }
    setPhase("idle");
    setJob(null);
    setJobStatus(null);
    setAnalysis(null);
    setError(null);
    setSelectedIds(new Set());
    setUploadProgress(0);
    if (fileInputRef.current) fileInputRef.current.value = "";
  }

  function chooseMethod(nextMethod: SplitMethod) {
    setMethod(nextMethod);
    const available = analysis ? candidatesForMethod(analysis.splitCandidates, nextMethod) : [];
    setSelectedIds(
      new Set(nextMethod === "manual" ? [] : available.map((candidate) => candidate.id)),
    );
  }

  function toggleBoundary(id: string) {
    if (!analysis) return;
    const next = manualSelection(analysis.splitCandidates, selectedIds);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    setMethod("manual");
    setSelectedIds(next);
  }

  const maxSizeMb = Math.round((capabilities?.maxUploadBytes ?? 52_428_800) / 1_048_576);
  const expiry = jobStatus?.expiresAt ?? job?.expiresAt;
  const workspaceVisible = Boolean(
    analysis && (phase === "ready" || phase === "processing" || phase === "completed"),
  );

  return (
    <main>
      <header
        className={`mx-auto flex items-center justify-between px-5 py-5 sm:px-8 ${
          workspaceVisible ? "max-w-[1600px]" : "max-w-6xl"
        }`}
      >
        <button
          className="flex items-center gap-3 disabled:opacity-50"
          disabled={phase === "uploading" || phase === "analyzing" || phase === "processing"}
          onClick={reset}
          aria-label="Kembali ke awal"
        >
          <span className="grid h-10 w-10 place-items-center rounded-xl bg-[#174f3c] text-xl font-black text-[#dff05b]">
            P
          </span>
          <span className="text-lg font-black tracking-tight">PilahDok</span>
        </button>
        {workspaceVisible ? (
          <button
            className="rounded-xl border border-[#b9c9c1] bg-white px-4 py-2 text-sm font-bold text-[#174f3c] hover:bg-[#edf3ef]"
            onClick={reset}
            disabled={phase === "processing"}
          >
            Pilih dokumen lain
          </button>
        ) : (
          <nav
            className="flex gap-5 text-sm font-semibold text-[#52615b]"
            aria-label="Navigasi utama"
          >
            <a href="#cara-kerja" className="hidden hover:text-[#174f3c] sm:block">
              Cara kerja
            </a>
            <a href="#privasi" className="hover:text-[#174f3c]">
              Privasi
            </a>
          </nav>
        )}
      </header>

      <section
        className={`mx-auto px-5 pb-12 sm:px-8 ${
          workspaceVisible ? "max-w-[1600px] pt-2" : "max-w-6xl pt-8 sm:pt-16"
        }`}
      >
        <div
          className={
            workspaceVisible
              ? "block"
              : "grid items-start gap-10 lg:grid-cols-[0.9fr_1.1fr] lg:gap-14"
          }
        >
          {!workspaceVisible && (
            <div className="pt-4">
              <span className="inline-flex rounded-full border border-[#174f3c]/20 bg-white/70 px-3 py-1 text-xs font-bold uppercase tracking-[0.16em] text-[#174f3c]">
                Word splitter yang transparan
              </span>
              <h1 className="display-font mt-6 text-5xl leading-[0.98] text-[#174f3c] sm:text-7xl">
                Pisahkan dokumen,
                <span className="block text-[#df6a43]">tanpa repot.</span>
              </h1>
              <p className="mt-6 max-w-xl text-base leading-7 text-[#5c6b65] sm:text-lg">
                Bagi DOCX berdasarkan heading, pola BAB, section, atau page break. Tanpa akun, tanpa
                riwayat permanen.
              </p>
              <div className="mt-7 flex flex-wrap gap-3 text-sm font-semibold">
                <span className="rounded-full bg-[#e5eee9] px-3 py-2">DOCX tetap editable</span>
                <span className="rounded-full bg-[#f4e8e1] px-3 py-2">Hapus otomatis</span>
                <span className="rounded-full bg-[#eef2cf] px-3 py-2">ZIP sekali unduh</span>
              </div>
            </div>
          )}

          <div
            className={`paper-card min-w-0 ${
              workspaceVisible ? "rounded-2xl p-4 sm:p-6" : "rounded-[2rem] p-4 sm:p-7"
            }`}
          >
            {error && (
              <div
                ref={errorRef}
                tabIndex={-1}
                role="alert"
                className="mb-5 rounded-2xl border border-red-200 bg-red-50 p-4 text-sm text-red-800"
              >
                <p className="font-bold">Proses belum berhasil</p>
                <p className="mt-1">{error}</p>
                <button className="mt-3 font-bold underline" onClick={reset}>
                  Mulai lagi
                </button>
                {job && (
                  <button className="ml-4 mt-3 font-bold underline" onClick={removeJob}>
                    Hapus file saya sekarang
                  </button>
                )}
              </div>
            )}

            {(phase === "idle" || phase === "deleted") && (
              <UploadPanel
                deleted={phase === "deleted"}
                dragActive={dragActive}
                maxSizeMb={maxSizeMb}
                fileInputRef={fileInputRef}
                onDragActive={setDragActive}
                onFile={begin}
              />
            )}

            {(phase === "uploading" || phase === "analyzing") && (
              <ProgressPanel
                phase={phase}
                progress={phase === "uploading" ? uploadProgress : (jobStatus?.progress ?? 10)}
                onCancel={phase === "uploading" ? () => uploadAbortRef.current?.abort() : undefined}
              />
            )}

            {analysis && (phase === "ready" || phase === "processing" || phase === "completed") && (
              <Workspace
                analysis={analysis}
                method={method}
                methodCandidates={methodCandidates}
                selectedIds={selectedIds}
                segments={segments}
                phase={phase}
                jobStatus={jobStatus}
                expiry={expiry}
                onMethod={chooseMethod}
                onToggle={toggleBoundary}
                onClear={() => {
                  setMethod("manual");
                  setSelectedIds(new Set());
                }}
                onProcess={processDocument}
                onDownload={download}
                onDelete={removeJob}
              />
            )}
          </div>
        </div>
      </section>

      {!workspaceVisible && (
        <>
          <section id="cara-kerja" className="border-y border-[#174f3c]/10 bg-[#174f3c] text-white">
            <div className="mx-auto grid max-w-6xl gap-8 px-5 py-14 sm:grid-cols-3 sm:px-8">
              {[
                [
                  "01",
                  "Unggah",
                  "File divalidasi sebagai paket DOCX, bukan hanya dari nama atau MIME.",
                ],
                ["02", "Tinjau", "Periksa struktur dan aktifkan batas pemisahan yang sesuai."],
                ["03", "Unduh", "Worker memvalidasi hasil lalu mengemas semuanya ke satu ZIP."],
              ].map(([number, title, copy]) => (
                <article key={number}>
                  <span className="text-sm font-black text-[#dff05b]">{number}</span>
                  <h2 className="mt-2 text-xl font-bold">{title}</h2>
                  <p className="mt-2 text-sm leading-6 text-white/70">{copy}</p>
                </article>
              ))}
            </div>
          </section>

          <section id="privasi" className="mx-auto max-w-6xl px-5 py-14 sm:px-8">
            <div className="grid gap-8 md:grid-cols-2">
              <div>
                <h2 className="display-font text-3xl text-[#174f3c]">
                  File singgah, bukan menetap.
                </h2>
                <p className="mt-3 text-sm leading-7 text-[#5c6b65]">
                  PilahDok tidak membuat akun dan tidak menyimpan riwayat. Input, hasil, ZIP, dan
                  metadata sementara dihapus otomatis paling lambat{" "}
                  {capabilities?.jobTtlMinutes ?? 60} menit, atau segera saat Anda menekan tombol
                  hapus.
                </p>
              </div>
              <div className="rounded-2xl border border-amber-200 bg-amber-50 p-5 text-sm leading-6 text-amber-950">
                <strong>Batasan halaman Word:</strong> DOCX bukan format fixed-layout. Page break
                eksplisit berbeda dari halaman visual yang dihitung Microsoft Word. PilahDok tidak
                mengklaim split halaman visual akurat tanpa provider layout khusus.
              </div>
            </div>
          </section>

          <footer className="border-t border-[#174f3c]/10 px-5 py-7 text-center text-xs text-[#6b7772]">
            PilahDok · Pemrosesan sementara · Ketentuan penggunaan: unggah hanya dokumen yang berhak
            Anda proses.
          </footer>
        </>
      )}
    </main>
  );
}

function UploadPanel({
  deleted,
  dragActive,
  maxSizeMb,
  fileInputRef,
  onDragActive,
  onFile,
}: {
  deleted: boolean;
  dragActive: boolean;
  maxSizeMb: number;
  fileInputRef: React.RefObject<HTMLInputElement | null>;
  onDragActive: (active: boolean) => void;
  onFile: (file: File) => void;
}) {
  return (
    <div>
      {deleted && (
        <div
          role="status"
          className="mb-4 rounded-xl bg-[#e5eee9] p-3 text-sm font-semibold text-[#174f3c]"
        >
          Job dan seluruh file telah dihapus.
        </div>
      )}
      <div
        className={`grid min-h-80 place-items-center rounded-[1.5rem] border-2 border-dashed px-6 text-center transition ${
          dragActive ? "border-[#df6a43] bg-[#fff5ef]" : "border-[#aac2b7] bg-[#f8faf6]"
        }`}
        onDragEnter={(event) => {
          event.preventDefault();
          onDragActive(true);
        }}
        onDragOver={(event) => event.preventDefault()}
        onDragLeave={() => onDragActive(false)}
        onDrop={(event) => {
          event.preventDefault();
          onDragActive(false);
          const file = event.dataTransfer.files[0];
          if (file) onFile(file);
        }}
      >
        <div>
          <div className="mx-auto grid h-16 w-16 place-items-center rounded-2xl bg-[#174f3c] text-3xl text-[#dff05b]">
            ⇧
          </div>
          <h2 className="mt-5 text-xl font-black">Letakkan DOCX di sini</h2>
          <p className="mt-2 text-sm text-[#64716c]">atau pilih satu file dari perangkat Anda</p>
          <input
            ref={fileInputRef}
            className="sr-only"
            id="document-upload"
            type="file"
            accept=".docx,application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            onChange={(event) => {
              const file = event.target.files?.[0];
              if (file) onFile(file);
            }}
          />
          <label
            htmlFor="document-upload"
            className="mt-5 inline-flex cursor-pointer rounded-xl bg-[#b84526] px-5 py-3 font-bold text-white shadow-md hover:bg-[#96371d]"
          >
            Pilih file DOCX
          </label>
          <p className="mt-5 text-xs text-[#75807c]">
            Maksimal {maxSizeMb} MB · .doc dan .docm ditolak
          </p>
        </div>
      </div>
      <p className="mt-4 text-center text-xs leading-5 text-[#64716c]">
        File diproses sementara dan dapat Anda hapus kapan saja. Isi dokumen tidak dicatat ke log.
      </p>
    </div>
  );
}

function ProgressPanel({
  phase,
  progress,
  onCancel,
}: {
  phase: "uploading" | "analyzing";
  progress: number;
  onCancel?: () => void;
}) {
  const label = phase === "uploading" ? "Mengunggah secara aman" : "Menganalisis struktur DOCX";
  return (
    <div className="grid min-h-96 place-items-center px-4 text-center" aria-live="polite">
      <div className="w-full max-w-md">
        <div className="mx-auto h-16 w-16 animate-pulse rounded-2xl bg-[#dff05b] p-4 text-2xl">
          ⌁
        </div>
        <h2 className="mt-6 text-2xl font-black">{label}</h2>
        <p className="mt-2 text-sm text-[#64716c]">
          {phase === "uploading"
            ? "Data dikirim langsung ke ruang job sementara."
            : "Worker mencari heading, BAB, section, dan explicit page break."}
        </p>
        <div
          className="mt-7 h-3 overflow-hidden rounded-full bg-[#dbe4de]"
          aria-label={`Progress ${progress}%`}
        >
          <div
            className="progress-stripe h-full rounded-full bg-[#174f3c] transition-all"
            style={{ width: `${Math.max(4, progress)}%` }}
          />
        </div>
        <p className="mt-2 text-sm font-bold text-[#174f3c]">{progress}%</p>
        {onCancel && (
          <button className="mt-5 text-sm font-bold text-[#9d3f29] underline" onClick={onCancel}>
            Batalkan upload
          </button>
        )}
      </div>
    </div>
  );
}

function Workspace({
  analysis,
  method,
  methodCandidates,
  selectedIds,
  segments,
  phase,
  jobStatus,
  expiry,
  onMethod,
  onToggle,
  onClear,
  onProcess,
  onDownload,
  onDelete,
}: {
  analysis: AnalysisResult;
  method: SplitMethod;
  methodCandidates: AnalysisResult["splitCandidates"];
  selectedIds: Set<string>;
  segments: SplitSegment[];
  phase: "ready" | "processing" | "completed";
  jobStatus: JobStatus | null;
  expiry?: string;
  onMethod: (method: SplitMethod) => void;
  onToggle: (id: string) => void;
  onClear: () => void;
  onProcess: (segments: SplitSegment[]) => Promise<void>;
  onDownload: () => Promise<void>;
  onDelete: () => Promise<void>;
}) {
  if (phase === "completed") {
    return (
      <div className="grid min-h-96 place-items-center px-3 py-8 text-center" aria-live="polite">
        <div className="w-full max-w-md">
          <span className="mx-auto grid h-20 w-20 place-items-center rounded-full bg-[#dff05b] text-4xl">
            ✓
          </span>
          <h2 className="display-font mt-6 text-4xl text-[#174f3c]">Dokumen sudah terpilah.</h2>
          <p className="mt-3 text-sm leading-6 text-[#64716c]">
            Hasil DOCX telah dibuka ulang dan divalidasi, lalu dikemas dalam ZIP.
          </p>
          <button
            className="mt-7 w-full rounded-xl bg-[#174f3c] px-5 py-4 font-bold text-white shadow-lg hover:bg-[#0f3b2c]"
            onClick={onDownload}
          >
            Unduh ZIP {jobStatus?.resultSize ? `· ${formatBytes(jobStatus.resultSize)}` : ""}
          </button>
          <p className="mt-4 text-xs text-[#6f7b76]">Tersedia hingga {formatExpiry(expiry)}</p>
          <button className="mt-5 text-sm font-bold text-[#9d3f29] underline" onClick={onDelete}>
            Hapus file saya sekarang
          </button>
        </div>
      </div>
    );
  }

  return (
    <div>
      <div className="flex flex-wrap items-start justify-between gap-3 border-b border-[#dce5df] pb-5">
        <div>
          <p className="text-xs font-bold uppercase tracking-wider text-[#df6a43]">
            Analisis selesai
          </p>
          <h2 className="mt-1 text-2xl font-black">Pilih isi yang ingin dipisahkan</h2>
          <p className="mt-1 text-sm text-[#64716c]">
            Tandai awal file baru, periksa hasilnya, lalu pisahkan. File asli tetap utuh.
          </p>
        </div>
        <button
          type="button"
          onClick={() =>
            document
              .getElementById("split-results")
              ?.scrollIntoView({ behavior: "smooth", block: "start" })
          }
          className="rounded-full bg-[#e5eee9] px-4 py-2 text-sm font-bold text-[#174f3c]"
          disabled={phase === "processing"}
        >
          Lihat {segments.length} file hasil ↓
        </button>
      </div>

      <div className="my-5 rounded-xl bg-[#f0f4ed] p-4">
        <div
          className="flex flex-wrap items-center gap-2"
          role="group"
          aria-label="Bantuan memilih potongan"
        >
          <span className="mr-1 text-xs font-bold text-[#52615b]">Cara memilih:</span>
          {[
            methods.at(-1)!,
            ...methods
              .slice(0, 3)
              .filter((item) =>
                analysis.splitCandidates.some(
                  (candidate) => candidate.type === item.value && candidate.structureIndex > 0,
                ),
              ),
          ].map((item) => (
            <button
              key={item.value}
              type="button"
              aria-pressed={method === item.value}
              title={item.description}
              disabled={phase === "processing"}
              onClick={() => onMethod(item.value)}
              className={`min-h-10 rounded-lg border px-3 py-2 text-sm font-bold disabled:opacity-50 ${method === item.value ? "border-[#174f3c] bg-[#174f3c] text-white" : "border-[#cbd5ce] bg-white text-[#174f3c]"}`}
            >
              {item.label}
            </button>
          ))}
        </div>
        <p className="mt-2 text-xs leading-5 text-[#52615b]">
          {initialMethod(analysis.splitCandidates) === "manual"
            ? "Tidak perlu judul bab khusus. Pilih langsung dari isi dokumen di bawah."
            : method === "manual"
              ? "Anda bebas menambah atau membatalkan potongan pada isi dokumen."
              : "Potongan awal sudah dipilih otomatis. Anda tetap bisa mengubahnya langsung pada isi dokumen."}
        </p>
        {methods
          .slice(3, 5)
          .some((item) =>
            analysis.splitCandidates.some(
              (candidate) => candidate.type === item.value && candidate.structureIndex > 0,
            ),
          ) && (
          <details className="mt-2 text-xs text-[#52615b]">
            <summary className="cursor-pointer py-1 font-bold">Pilihan otomatis lainnya</summary>
            <div className="mt-2 flex flex-wrap gap-2">
              {methods
                .slice(3, 5)
                .filter((item) =>
                  analysis.splitCandidates.some(
                    (candidate) => candidate.type === item.value && candidate.structureIndex > 0,
                  ),
                )
                .map((item) => (
                  <button
                    key={item.value}
                    type="button"
                    aria-pressed={method === item.value}
                    disabled={phase === "processing"}
                    onClick={() => onMethod(item.value)}
                    className="rounded-lg border border-[#cbd5ce] bg-white px-3 py-2 text-left"
                  >
                    <strong>{item.label}</strong>
                    <span className="mt-1 block max-w-xs">{item.description}</span>
                  </button>
                ))}
            </div>
          </details>
        )}
      </div>

      <div className="grid min-w-0 items-start gap-5 lg:grid-cols-[minmax(0,1fr)_340px]">
        <StructurePicker
          analysis={analysis}
          selectedIds={selectedIds}
          outputCount={segments.length}
          disabled={phase === "processing"}
          onToggle={onToggle}
          onClear={onClear}
        />
        <div className="min-w-0 lg:sticky lg:top-5">
          {phase === "processing" ? (
            <div className="mt-6 rounded-2xl bg-[#174f3c] p-5 text-white" aria-live="polite">
              <div className="flex items-center justify-between text-sm font-bold">
                <span>Worker memproses dan memvalidasi hasil…</span>
                <span>{jobStatus?.progress ?? 5}%</span>
              </div>
              <div className="mt-3 h-2 overflow-hidden rounded-full bg-white/20">
                <div
                  className="progress-stripe h-full bg-[#dff05b] transition-all"
                  style={{ width: `${Math.max(5, jobStatus?.progress ?? 5)}%` }}
                />
              </div>
            </div>
          ) : (
            <SplitForm
              analysis={analysis}
              segments={segments}
              disabled={methodCandidates.length === 0 || segments.length < 2}
              onSubmit={onProcess}
            />
          )}
        </div>
      </div>
      <details className="mt-5 rounded-xl border border-[#dce5df] px-4 py-3 text-xs leading-6 text-[#64716c]">
        <summary className="cursor-pointer font-bold">
          Informasi dokumen & catatan sebelum membagikan hasil
        </summary>
        <p className="mt-2">
          {analysis.metadata.paragraphCount} paragraf · {analysis.metadata.tableCount} tabel ·{" "}
          {analysis.metadata.imageCount} gambar. Periksa kembali dokumen hasil sebelum
          membagikannya.
        </p>
        <ul className="mt-2 list-disc space-y-1 pl-4">
          {analysis.warnings.map((warning, index) => (
            <li key={index}>{warning}</li>
          ))}
        </ul>
      </details>
      <div className="mt-5 flex items-center justify-between gap-3 border-t border-[#dce5df] pt-4 text-xs text-[#6b7772]">
        <span>Hapus otomatis: {formatExpiry(expiry)}</span>
        <button className="font-bold text-[#9d3f29] underline" onClick={onDelete}>
          Hapus sekarang
        </button>
      </div>
    </div>
  );
}

function formatBytes(bytes: number): string {
  if (bytes < 1_024 * 1_024) return `${Math.ceil(bytes / 1_024)} KB`;
  return `${(bytes / 1_024 / 1_024).toFixed(1)} MB`;
}

function formatExpiry(value?: string): string {
  if (!value) return "maksimal 60 menit";
  return new Intl.DateTimeFormat("id-ID", { hour: "2-digit", minute: "2-digit" }).format(
    new Date(value),
  );
}
