"use client";

import { useEffect, useMemo, useState } from "react";
import { previewBlocks } from "@/lib/editor";
import type { AnalysisResult } from "@/lib/types";

export function StructurePicker({
  analysis,
  selectedIds,
  outputCount,
  disabled,
  onToggle,
  onClear,
}: {
  analysis: AnalysisResult;
  selectedIds: ReadonlySet<string>;
  outputCount: number;
  disabled: boolean;
  onToggle: (id: string) => void;
  onClear: () => void;
}) {
  const blocks = useMemo(() => previewBlocks(analysis), [analysis]);
  const [query, setQuery] = useState("");
  const [matchIndex, setMatchIndex] = useState(0);
  const [visibleCount, setVisibleCount] = useState(60);
  const [largeText, setLargeText] = useState(false);
  const selectedStructures = useMemo(
    () =>
      new Set(
        analysis.splitCandidates
          .filter((item) => selectedIds.has(item.id) && item.structureIndex > 0)
          .map((item) => item.structureIndex),
      ),
    [analysis, selectedIds],
  );
  const matches = useMemo(() => {
    const value = query.trim().toLocaleLowerCase("id-ID");
    return value
      ? blocks
          .map((block, index) => ({ block, index }))
          .filter(({ block }) =>
            block.paragraphs.some((item) =>
              (item.previewText ?? item.text).toLocaleLowerCase("id-ID").includes(value),
            ),
          )
      : [];
  }, [blocks, query]);
  const target = matches[matchIndex % Math.max(1, matches.length)];

  useEffect(() => {
    if (target)
      document
        .getElementById(`content-${target.block.structureIndex}`)
        ?.scrollIntoView({ block: "nearest", behavior: "smooth" });
  }, [target, visibleCount]);

  function find(value: string, direction = 0) {
    const normalized = value.trim().toLocaleLowerCase("id-ID");
    const found = normalized
      ? blocks
          .map((block, index) => ({ block, index }))
          .filter(({ block }) =>
            block.paragraphs.some((item) =>
              (item.previewText ?? item.text).toLocaleLowerCase("id-ID").includes(normalized),
            ),
          )
      : [];
    const next = direction
      ? (matchIndex + direction + found.length) % Math.max(1, found.length)
      : 0;
    setQuery(value);
    setMatchIndex(next);
    if (found[next]) setVisibleCount((current) => Math.max(current, found[next].index + 1));
  }

  let fileNumber = 1;
  return (
    <section
      className="min-w-0 overflow-hidden rounded-2xl border border-[#d5dfd9] bg-[#edf1ec]"
      aria-labelledby="preview-title"
    >
      <div className="border-b border-[#d5dfd9] bg-white px-4 py-4 sm:px-5">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h3 id="preview-title" className="font-black text-[#174f3c]">
            1. Pilih lokasi potong
          </h3>
          <button
            type="button"
            className="min-h-10 rounded-lg border border-[#cbd5ce] px-3 text-xs font-bold text-[#52615b] disabled:opacity-40"
            disabled={disabled || outputCount < 2}
            onClick={onClear}
          >
            Hapus semua potongan
          </button>
        </div>
        <p className="mt-2 text-sm leading-6 text-[#52615b]">
          Baca isi dokumen, lalu klik <strong>Potong di sini</strong> di awal isi yang ingin
          dijadikan file baru.
        </p>
        <div className="mt-3 flex items-center gap-2">
          <label htmlFor="content-search" className="sr-only">
            Cari teks di dokumen
          </label>
          <input
            id="content-search"
            type="search"
            placeholder="Cari teks di dokumen…"
            className="min-w-0 flex-1 rounded-lg border border-[#cbd5ce] px-3 py-2 text-sm"
            value={query}
            onChange={(event) => find(event.target.value)}
          />
          {query && (
            <>
              <span role="status" className="shrink-0 text-xs text-[#52615b]">
                {matches.length ? `${matchIndex + 1}/${matches.length}` : "Tidak ditemukan"}
              </span>
              <button
                type="button"
                aria-label="Hasil pencarian berikutnya"
                className="min-h-10 min-w-10 rounded-lg border border-[#cbd5ce] font-bold disabled:opacity-40"
                disabled={!matches.length}
                onClick={() => find(query, 1)}
              >
                ↓
              </button>
            </>
          )}
          <button
            type="button"
            aria-label="Perbesar teks pratinjau"
            aria-pressed={largeText}
            className="min-h-10 min-w-10 rounded-lg border border-[#cbd5ce] bg-white px-2 text-sm font-bold"
            onClick={() => setLargeText((value) => !value)}
          >
            A{largeText ? "−" : "+"}
          </button>
        </div>
      </div>
      <div className="px-4 py-2 text-xs leading-5 text-[#596a61]">
        Pratinjau isi, bukan nomor halaman Word. Tata letak dan sebagian gambar dapat berbeda dari
        dokumen asli.
      </div>
      <div
        className="max-h-[72vh] overflow-y-auto overscroll-contain px-3 pb-6 sm:px-6"
        role="region"
        aria-label="Isi dokumen dan lokasi potong"
        tabIndex={0}
      >
        {blocks.slice(0, visibleCount).map((block, index) => {
          const cut = selectedStructures.has(block.structureIndex);
          if (cut) fileNumber++;
          const number = fileNumber;
          const table = block.paragraphs.some((item) => item.isInsideTable);
          return (
            <div
              key={block.structureIndex}
              id={`content-${block.structureIndex}`}
              data-preview-block={block.structureIndex}
              className="scroll-mt-4"
            >
              {index > 0 && block.boundary && (
                <div className="flex items-center gap-2 py-3">
                  <span
                    className={`flex-1 border-t border-dashed ${cut ? "border-[#267553]" : "border-[#bccbc0]"}`}
                  />
                  <button
                    type="button"
                    aria-label={`${cut ? "Batalkan potongan" : "Potong"} sebelum ${block.boundary.title}`}
                    aria-pressed={cut}
                    disabled={disabled}
                    onClick={() => onToggle(block.boundary!.id)}
                    className={`min-h-10 rounded-full border px-4 py-2 text-xs font-bold transition disabled:opacity-50 ${cut ? "border-[#174f3c] bg-[#174f3c] text-white" : "border-[#c2cec5] bg-white text-[#174f3c] hover:border-[#174f3c] hover:bg-[#e4eee7]"}`}
                  >
                    <span aria-hidden="true">{cut ? "✓" : "✂"} </span>
                    {cut ? "Batalkan potongan" : "Potong di sini"}
                  </button>
                  <span
                    className={`flex-1 border-t border-dashed ${cut ? "border-[#267553]" : "border-[#bccbc0]"}`}
                  />
                </div>
              )}
              <article
                className={`border-x border-l-4 border-[#dce5df] bg-white px-4 py-4 sm:px-7 ${number % 2 ? "border-l-[#267553]" : "border-l-[#bc613c]"} ${index === 0 || cut ? "rounded-t-xl border-t pt-5" : ""} ${target?.block.structureIndex === block.structureIndex ? "ring-2 ring-inset ring-[#dfb342]" : ""}`}
              >
                {(index === 0 || cut) && (
                  <div className="mb-4 flex items-center gap-2">
                    <span
                      className={`rounded-full px-3 py-1 text-xs font-black ${number % 2 ? "bg-[#e4eee7] text-[#174f3c]" : "bg-[#fae8df] text-[#813e26]"}`}
                    >
                      File {number}
                    </span>
                    <span className="text-xs text-[#64716c]">
                      {index === 0 ? "Awal dokumen" : "File baru dimulai di sini"}
                    </span>
                  </div>
                )}
                {table && (
                  <p className="mb-2 text-xs font-bold text-[#64716c]">
                    Isi tabel · dipertahankan sebagai satu bagian
                  </p>
                )}
                <div
                  className={
                    table ? "space-y-2 rounded-lg border border-[#d5dfd9] p-3" : "space-y-3"
                  }
                >
                  {block.paragraphs.map((paragraph) => (
                    <p
                      key={paragraph.id}
                      className={`whitespace-pre-wrap break-words leading-relaxed ${paragraph.headingLevel ? "text-lg font-bold text-[#233a2d]" : largeText ? "text-lg text-[#34473b]" : "text-sm text-[#34473b]"}`}
                    >
                      {paragraph.previewText ||
                        paragraph.text ||
                        (block.images.length ? null : (
                          <span className="text-xs italic text-[#718079]">Baris kosong</span>
                        ))}
                    </p>
                  ))}
                </div>
                {block.images.map((url, imageIndex) => (
                  <figure key={imageIndex} className="mt-4">
                    {/* eslint-disable-next-line @next/next/no-img-element -- bounded, validated embedded raster data; no remote requests */}
                    <img
                      src={url}
                      alt={`Gambar dalam dokumen, bagian ${block.structureIndex + 1}`}
                      loading="lazy"
                      className="max-h-80 max-w-full rounded-lg border border-[#e0e6df] object-contain"
                    />
                  </figure>
                ))}
              </article>
            </div>
          );
        })}
        {visibleCount < blocks.length && (
          <button
            type="button"
            className="mt-5 w-full rounded-xl border border-[#b9c9c1] bg-white px-4 py-3 text-sm font-bold text-[#174f3c]"
            onClick={() => setVisibleCount((value) => value + 60)}
          >
            Tampilkan isi berikutnya · {blocks.length - visibleCount} bagian lagi
          </button>
        )}
        {blocks.length === 0 && (
          <p className="rounded-xl bg-white p-6 text-sm text-[#52615b]">
            Isi dokumen tidak dapat ditampilkan sebagai teks. Coba dokumen dengan paragraf yang
            dapat diedit.
          </p>
        )}
      </div>
    </section>
  );
}
