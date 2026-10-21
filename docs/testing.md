# Pengujian PilahDok

## Menjalankan gate

Prasyarat: Node.js 24, .NET SDK 10, Redis, dan koneksi internet untuk dependency/Chromium pertama kali. Hentikan launcher lokal agar DLL tidak terkunci.

```powershell
.\scripts\test-all.ps1
```

Git Bash Windows: `./scripts/test-all.sh`. Gate native memakai port terpisah 13000/18080/18081/16380, lalu menghentikan server pengujian dalam `finally`.

Gate mencakup pengaman cleaner, restore NuGet terkunci, formatter, build solution, backend tests, audit NuGet, npm ci, Prettier, ESLint, Vitest, audit npm runtime, production build/TypeScript, generator fixture dengan Open XML Validator, dan Playwright Chromium.

## File pengujian yang dipertahankan

| Lokasi | Kegunaan |
| --- | --- |
| `tests/PilahDok.Tests/Unit` | Parser, nama output, token, TTL, dan cleanup |
| `tests/PilahDok.Tests/Integration` | Upload sampai download/delete, validasi hasil, relationship, dan recovery worker |
| `tests/PilahDok.Tests/Security` | File palsu, ZIP/XML berbahaya, path traversal, token, expiry, dan batas input |
| `tests/PilahDok.Tests/Support` | Host API pengujian dan controlled test doubles |
| `tests/PilahDok.Testing` | Pembuat dokumen dari source, dipakai backend dan generator |
| `apps/web/lib/*.test.ts` | Segmentasi editor, nama/rentang hasil, serta upload/error handling |
| `apps/web/e2e` | Browser desktop/mobile, tanpa Heading, keyboard, split/download, reset/delete |
| `scripts/test-clean.ps1` | Dry-run, perlindungan source/config/lockfile, data opt-in, fixture generasi, dan penolakan junction |

Semua project di `tests` direferensikan oleh solution atau generator; menghapusnya akan menghilangkan regresi dan merusak gate. Folder `tests` tidak diterbitkan ke runtime production.

## Fixture dibuat otomatis

Repository hanya menyimpan pembuat fixture, bukan DOCX hasilnya. `start-local.ps1 -TestE2E` dan gate native membuat `tests/fixtures/sample-structural.docx` serta `sample-no-headings.docx` melalui generator. Fixture container dibuat pada stage `fixtures` di `tests/Dockerfile`.

Untuk membuat fixture secara manual dari root:

```powershell
dotnet restore PilahDok.slnx --locked-mode
dotnet run --project tools/PilahDok.FixtureGenerator -c Release --no-restore -- tests/fixtures/sample-structural.docx
dotnet run --project tools/PilahDok.FixtureGenerator -c Release --no-restore -- tests/fixtures/sample-no-headings.docx --no-headings
```

Generator membuka dan memvalidasi kembali output, serta gagal jika ditemukan error Open XML. Cleaner menghapus folder fixture hasil generasi; source `FixtureDocumentBuilder.cs` tetap disimpan.

## Audit dan batas bukti

Audit runtime npm adalah gate wajib. Audit lengkap diekspor ke `artifacts/npm-audit.json` dan memberi warning untuk dev tooling. Verifikasi sebelumnya menemukan lima high pada rantai `braces → micromatch → fast-glob → @next/eslint-plugin-next → eslint-config-next`; runtime npm 0 dan NuGet tanpa temuan. Status aktual mengikuti hasil audit saat gate dijalankan, bukan jaminan bebas kerentanan.

Verifikasi 22 Oktober 2026 setelah konsolidasi dokumentasi dan fixture otomatis: `scripts/test-all.ps1` selesai exit 0 dari kondisi tanpa dependency/build/fixture. Pengaman cleaner lulus; 44/44 backend, 9/9 frontend, 5/5 browser (26,9 detik), format, lint, TypeScript, dan build lulus. Kedua fixture dibuat serta divalidasi otomatis. Audit runtime npm 0, NuGet tanpa temuan, lima high dev tooling tetap diperingatkan. Pemeriksaan tautan Markdown lokal serta sintaks Bash lulus. Artefak hasil gate dibersihkan sebelum upload source.

Docker opsional: `.\scripts\test-all.ps1 -Docker`; Bash Linux/macOS memakai gate container. Gate container memiliki audit lengkap yang lebih ketat dan dapat gagal karena temuan dev tooling tersebut. Pada verifikasi yang sama Compose config exit 0; compose up dan ps exit 1 karena daemon tidak aktif. Perubahan stage fixture container belum diverifikasi melalui build Docker.

Linux server target, ClamAV nyata, Microsoft Word desktop, corpus pengguna, load test, dan fuzzing luas belum diuji. Validasi OOXML serta fixture bukan bukti kesetaraan format Word untuk seluruh dokumen.

## Artefak

Log, hasil build, audit JSON, report Playwright, serta fixture generasi diabaikan Git. Hapus dengan `scripts/clean.ps1`; tambahkan `-Dependencies -JobData` untuk kembali ke source bersih. Artefak tidak perlu diunggah ke repository.
