# Panduan Agen PilahDok

## Sumber kebenaran

1. `AGENTS.md` memuat aturan eksekusi dan batas produk yang wajib dipertahankan.
2. `README.md` menjelaskan kemampuan produk dan cara menjalankan; kontrak HTTP ada di `docs/api.md`.
3. Asumsi, konflik yang sudah diselesaikan, dan keputusan teknis dirangkum di `docs/architecture.md`.

## Batas produk yang harus dipertahankan

- Jangan menambah login, akun, database permanen, atau riwayat dokumen.
- Jangan log isi dokumen, nama file asli, access token, atau download token.
- Structural split adalah kemampuan utama. Jangan menyebut explicit page break sebagai halaman visual.
- Visual pagination harus melalui `IWordPaginationEngine`, capability, dan feature flag; unavailable adalah default yang jujur.
- Seluruh input/output/ZIP/metadata harus dapat dihapus manual dan melalui hard TTL.
- Jangan meninggalkan fungsi inti sebagai TODO/placeholder.

## Struktur

- `apps/web`: Next.js/React/TypeScript strict.
- `apps/api`: ASP.NET Core minimal API dan kontrak HTTP.
- `apps/worker`: consumer queue dan cleanup service.
- `libs/PilahDok.Core`: domain serta interface tanpa detail infrastruktur.
- `libs/PilahDok.Infrastructure`: Redis, filesystem, security validation, dan Open XML engine.
- `tests`: fixture builder, unit, integration, security, dan E2E.

## Aturan perubahan

- Jaga DTO terpisah dari domain model dan semua I/O async/cancellable.
- Validasi batas path, package, XML, dan nama file pada trust boundary.
- Output DOCX wajib dibuka dan divalidasi ulang sebelum ZIP dipublikasikan.
- Dependency harus dikunci; base container harus memakai tag spesifik dan digest.
- Tambahkan atau sesuaikan test ketika lifecycle, parser, endpoint, atau kebijakan security berubah.
- Dokumentasikan asumsi baru dan keputusan teknis yang tidak trivial.

## Perintah wajib sebelum handoff

```powershell
.\scripts\test-all.ps1
docker compose config --quiet
docker compose up --build -d
docker compose ps
```

Jangan menyatakan gate lulus sebelum perintahnya benar-benar selesai dengan exit code 0. Gunakan `docker compose down --volumes` hanya bila memang bermaksud menghapus seluruh job lokal.
