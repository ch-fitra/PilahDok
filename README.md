# PilahDok

Pisahkan satu dokumen Word `.docx` menjadi beberapa file yang tetap dapat diedit, tanpa akun. Pilih lokasi potong langsung dari isi dokumen atau gunakan bantuan otomatis per bab, lalu unduh semua hasil sebagai ZIP.

## Mulai tanpa Docker

Prasyarat Windows: **Git, Node.js 24, .NET SDK 10, dan `redis-server` di PATH**. Aplikasi menggunakan Next.js, API/worker .NET, dan Redis sementara. Hosting statis atau PHP saja tidak cukup.

Di **Git Bash**:

```bash
git clone https://github.com/ch-fitra/PilahDok.git
cd PilahDok
./scripts/start-local.sh
```

Di **PowerShell**, setelah masuk folder project:

```powershell
.\scripts\start-local.ps1
```

Atau klik dua kali **Mulai-PilahDok.cmd**. Jika execution policy memblokir skrip, gunakan:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-local.ps1
```

Launcher memasang dependency yang belum ada, melakukan restore terkunci, membangun web/API/worker, dan menjalankan Redis privat tanpa persistence. Penggunaan pertama memerlukan internet dan waktu untuk memasang dependency.

Buka **http://localhost:3000**. API memakai port 8080, worker health 8081, Redis privat 6380. Tekan **Ctrl+C** untuk menghentikan proses launcher. Jika port terpakai, hentikan service tersebut atau pilih port lain melalui opsi launcher. Jangan gunakan `-SkipBuild` pada clone baru atau setelah mengubah URL/port API.

Untuk **server Linux tanpa Docker**, ikuti [deployment native](docs/deployment-native.md), termasuk systemd, Nginx, TLS, Redis, dan temporary storage.

## Cara menggunakan

1. Unggah satu file `.docx`.
2. Tunggu analisis selesai, lalu baca pratinjau teks/gambar.
3. Klik **Potong di sini** pada awal isi yang ingin menjadi file baru. Dokumen tanpa Heading juga dapat dipotong.
4. Periksa daftar hasil dan ubah nama bila diperlukan. Klik **Batalkan potongan** untuk menggabungkan kembali.
5. Proses pemisahan, unduh ZIP, lalu pilih **Hapus file saya sekarang** bila selesai.

File asli tidak diubah. Metode struktural mencakup Heading 1/2, pola BAB/CHAPTER, section break, explicit page break, dan pilihan manual. Output DOCX dibuka serta divalidasi ulang sebelum ZIP diterbitkan.

**Batasan:** pratinjau bukan halaman visual Microsoft Word. Explicit page break hanya berarti batas yang tersimpan dalam dokumen. Pagination visual/PDF belum tersedia. Pemisahan mempertahankan package asli, sehingga media, komentar, atau metadata dari bagian lain dapat tetap tersimpan; periksa hasil sebelum membagikan dokumen sensitif.

## Struktur repository

| Folder | Fungsi |
| --- | --- |
| `apps/web` | Antarmuka Next.js/React |
| `apps/api` | Endpoint HTTP, validasi, dan akses job |
| `apps/worker` | Pemrosesan dokumen dan cleanup TTL |
| `libs` | Domain, Open XML engine, Redis, filesystem, dan keamanan |
| `tests` | Regresi backend, integrasi, keamanan, serta pembuat fixture |
| `tools` | Generator DOCX pengujian yang dapat direproduksi |
| `scripts` | Launcher, pengujian, dan pembersihan |
| `docs` | Lima panduan teknis untuk pengembangan/deployment |

`tests` diperlukan untuk memverifikasi perubahan, bukan untuk menjalankan layanan production. DOCX fixture dibuat otomatis saat E2E, sehingga repository tidak memuat dokumen contoh biner. Tidak ada akun, database permanen, atau riwayat dokumen.

## Pengujian

Hentikan launcher aplikasi terlebih dahulu, lalu jalankan:

```powershell
.\scripts\test-all.ps1
```

Di Git Bash Windows: `./scripts/test-all.sh`. Gate menjalankan formatter, build, lint, unit/integration/security, audit dependency, pembuatan fixture, dan browser E2E tanpa Docker. Rincian, keterbatasan, serta status verifikasi ada di [panduan pengujian](docs/testing.md).

## Pembersihan

Hentikan aplikasi. Lihat daftar target sebelum menghapus hasil build/cache/laporan test:

```powershell
.\scripts\clean.ps1 -DryRun
.\scripts\clean.ps1
```

Untuk juga menghapus dependency dan seluruh dokumen lokal:

```powershell
.\scripts\clean.ps1 -Dependencies -JobData
```

**`-JobData` menghapus unggahan dan hasil lokal; unggah ulang bila diperlukan.** Di Git Bash gunakan `./scripts/clean.sh` dengan opsi yang sama. Source, pembuat fixture, lockfile, `.env`, dan Git tetap disimpan. Launcher memasang ulang dependency saat dibutuhkan. Pembersihan tidak menyentuh volume Docker atau storage server di luar project.

Log, dependency, data job, fixture hasil generasi, laporan tes, serta build tidak disertakan di GitHub. Log operasional baru dibuat ketika aplikasi dijalankan.

## Konfigurasi dan dokumentasi

Launcher lokal mengisi path, koneksi Redis, URL API, dan port secara otomatis. Untuk layanan native gunakan environment .NET seperti `PilahDok__StorageRoot` dan `PilahDok__RedisConnection`. `.env.example` adalah template Compose; `.env` tidak dibaca otomatis oleh service .NET native. `NEXT_PUBLIC_API_URL` ditanam saat build web, sehingga perubahan domain/port memerlukan build ulang.

- [Deployment Windows/Linux](docs/deployment-native.md)
- [Arsitektur, keputusan, dan batas produk](docs/architecture.md)
- [Kontrak API](docs/api.md)
- [Keamanan, privasi, dan retensi](docs/security.md)
- [Pengujian dan status verifikasi](docs/testing.md)

`AGENTS.md` menyimpan aturan pengembangan dan batas produk. Keputusan teknis dirangkum di panduan arsitektur.

## Docker opsional

```powershell
Copy-Item .env.example .env
docker compose up --build -d
docker compose ps
```

Hentikan dengan `docker compose down`. Tambahkan `--volumes` hanya jika ingin menghapus seluruh data job pada volume Docker. Docker tidak diperlukan untuk launcher Windows maupun deployment native Linux.
