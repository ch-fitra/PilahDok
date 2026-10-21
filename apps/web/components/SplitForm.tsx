"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useEffect, useMemo, useRef } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { outputSummaries } from "@/lib/editor";
import type { AnalysisResult, SplitSegment } from "@/lib/types";

const schema = z.object({
  segments: z
    .array(
      z.object({
        outputFileName: z
          .string()
          .trim()
          .min(1, "Nama file wajib diisi.")
          .max(140, "Nama file maksimal 140 karakter."),
      }),
    )
    .min(1)
    .max(200),
});
type FormData = z.infer<typeof schema>;

export function SplitForm({
  analysis,
  segments,
  disabled,
  onSubmit,
}: {
  analysis: AnalysisResult;
  segments: SplitSegment[];
  disabled: boolean;
  onSubmit: (segments: SplitSegment[]) => Promise<void>;
}) {
  const summaries = useMemo(() => outputSummaries(analysis, segments), [analysis, segments]);
  const renamed = useRef(new Map<number, string>());
  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<FormData>({ resolver: zodResolver(schema), defaultValues: { segments } });
  useEffect(
    () =>
      reset({
        segments: summaries.map((item) => ({
          outputFileName: renamed.current.get(item.startIndex) ?? item.segment.outputFileName,
        })),
      }),
    [summaries, reset],
  );

  return (
    <form
      id="split-results"
      className="scroll-mt-5 overflow-hidden rounded-2xl border border-[#d5dfd9] bg-white"
      onSubmit={handleSubmit(async (data) =>
        onSubmit(
          data.segments.map((item, index) => ({
            startCandidateId: segments[index].startCandidateId,
            outputFileName: item.outputFileName,
          })),
        ),
      )}
    >
      <div className="border-b border-[#dce5df] p-4 sm:p-5">
        <h3 className="font-black text-[#174f3c]">2. Periksa file hasil</h3>
        <p role="status" className="mt-2 text-sm leading-6 text-[#52615b]">
          {disabled ? (
            "Belum ada potongan. Pilih awal file baru pada pratinjau."
          ) : (
            <>
              Dokumen akan menjadi <strong>{segments.length} file Word</strong>, diunduh dalam satu
              ZIP.
            </>
          )}
        </p>
      </div>
      <div
        className="max-h-[48vh] space-y-3 overflow-y-auto p-4 sm:p-5"
        aria-label="Daftar file hasil"
      >
        {summaries.map((item, index) => (
          <article
            key={item.startIndex}
            className={`rounded-xl border border-l-4 border-[#dce5df] bg-[#fcfdfb] p-3 ${index % 2 ? "border-l-[#bc613c]" : "border-l-[#267553]"}`}
          >
            <div className="flex items-center gap-2">
              <span
                aria-hidden="true"
                className={`grid h-8 w-7 shrink-0 place-items-center rounded-md text-xs font-black ${index % 2 ? "bg-[#fae8df] text-[#813e26]" : "bg-[#e4eee7] text-[#174f3c]"}`}
              >
                {index + 1}
              </span>
              <div>
                <h4 className="text-sm font-bold text-[#253c2e]">File {index + 1}</h4>
                <p className="text-[11px] text-[#64716c]">{item.range}</p>
              </div>
            </div>
            <p className="mt-3 line-clamp-2 break-words text-sm leading-6 text-[#43564a]">
              {item.title}
            </p>
            <details
              className="mt-2 text-xs text-[#52615b]"
              open={Boolean(errors.segments?.[index]?.outputFileName) || undefined}
            >
              <summary className="cursor-pointer py-1 font-bold">
                Ubah nama file {index + 1}
              </summary>
              <label className="mt-2 block">
                <span className="mb-1 block">Nama file {index + 1}</span>
                <input
                  {...register(`segments.${index}.outputFileName`)}
                  onBlur={(event) => {
                    renamed.current.set(item.startIndex, event.target.value);
                  }}
                  className="w-full rounded-lg border border-[#cbd5ce] bg-white px-3 py-2 text-sm"
                  aria-invalid={Boolean(errors.segments?.[index]?.outputFileName)}
                />
              </label>
              {errors.segments?.[index]?.outputFileName && (
                <p role="alert" className="mt-1 text-red-800">
                  {errors.segments[index]?.outputFileName?.message}
                </p>
              )}
            </details>
          </article>
        ))}
      </div>
      <div className="border-t border-[#dce5df] bg-[#f5f8f1] p-4 sm:p-5">
        {segments.length > 200 && (
          <p role="alert" className="mb-3 text-sm text-red-800">
            Maksimal 200 file per proses. Batalkan beberapa potongan.
          </p>
        )}
        <button
          className="w-full rounded-xl bg-[#174f3c] px-4 py-3.5 text-sm font-bold text-white shadow-sm hover:bg-[#0f3b2c] disabled:cursor-not-allowed disabled:opacity-45"
          disabled={disabled || isSubmitting || segments.length > 200}
          type="submit"
        >
          {isSubmitting
            ? "Menyiapkan dokumen…"
            : disabled
              ? "Pilih lokasi potong dahulu"
              : `Pisahkan menjadi ${segments.length} file`}
        </button>
        <p className="mt-3 text-center text-xs leading-5 text-[#64716c]">
          Seluruh isi tetap disertakan. Anda hanya menentukan pembagian file.
        </p>
      </div>
    </form>
  );
}
