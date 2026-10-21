# Menjalankan PilahDok tanpa Docker

PilahDok tetap aplikasi server: browser mengakses Next.js dan API; pemrosesan dilakukan worker .NET, dengan Redis sementara dan direktori file bersama. Hosting statis/PHP saja tidak dapat menjalankan aplikasi ini. LibreOffice/Gotenberg tidak diperlukan untuk structural split.

## Windows lokal

Prasyarat: Node.js 24, .NET SDK 10, dan `redis-server` di PATH. Launcher juga mengenali SDK lokal di `%LOCALAPPDATA%/PilahDok/dotnet`. Redis Windows lama hanya digunakan untuk pengujian lokal, bukan acuan deployment produksi; gunakan Redis yang masih didukung pada Linux untuk server.

```powershell
cd D:\Project\Pilahdok
.\scripts\start-local.ps1
```

Buka http://localhost:3000. Launcher memasang dependency web jika belum ada, membangun API/worker/web, menjalankan Redis privat pada 127.0.0.1:6380 tanpa persistence, menunggu readiness, dan mengawasi semua proses. API: 8080; worker health: 8081. Ctrl+C menghentikan hanya proses yang dibuat launcher; file sementara tidak dihapus massal. Log operasional dan artefak lokal berada di `.pilahdok-data/` yang diabaikan Git. Cleanup berjalan kembali saat worker dimulai. Port yang sudah digunakan menyebabkan launcher berhenti dengan pesan; tidak mengambil alih service lain.

Untuk Git Bash Windows jalankan `./scripts/start-local.sh`. Untuk membuka lewat File Explorer, klik dua kali `Mulai-PilahDok.cmd` pada root proyek. Launcher Bash ini bukan launcher server Linux; gunakan langkah systemd di bawah untuk Linux.

Jika execution policy lokal memblokir skrip:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-local.ps1
```

`-SkipBuild` hanya untuk build yang sudah cocok dengan port API yang dipilih. `NEXT_PUBLIC_API_URL` ditanam saat build, sehingga perubahan port/domain wajib diikuti build ulang. Launcher tanpa `-SkipBuild` menangani hal ini.

## Linux produksi pada satu server

Contoh ini memakai `/opt/pilahdok` untuk source/rilis, user service `pilahdok`, Redis lokal, Nginx, systemd, dan `/var/lib/pilahdok/jobs` untuk temporary storage. Sesuaikan domain, lokasi executable, sertifikat TLS, dan kapasitas server. Konfigurasi Linux ini belum dieksekusi pada mesin Windows audit.

1. Install Node.js 24, .NET SDK/runtime 10, Redis yang didukung, Nginx, dan ClamAV melalui paket resmi sistem. Buat user service tanpa login serta direktori storage yang hanya dapat diakses user service. Jangan menaruh storage di webroot atau backup.
2. Redis harus bind hanya loopback, `protected-mode yes`, `save ""`, `appendonly no`. Gunakan instance khusus agar perubahan persistence tidak memengaruhi aplikasi lain.
3. Salin repository ke `/opt/pilahdok`, lalu build dengan versi dependency yang terkunci:

```bash
cd /opt/pilahdok
dotnet restore PilahDok.slnx --locked-mode
dotnet publish apps/api/PilahDok.Api.csproj -c Release --no-restore -o /opt/pilahdok/release/api
dotnet publish apps/worker/PilahDok.Worker.csproj -c Release --no-restore -o /opt/pilahdok/release/worker
cd apps/web
npm ci
NEXT_PUBLIC_API_URL=https://pilahdok.example.com/api/v1 npm run build
```

4. Buat `/etc/pilahdok.env`, dimiliki administrator dan hanya dapat dibaca service yang perlu. Contoh isi:

```ini
DOTNET_ENVIRONMENT=Production
PilahDok__RedisConnection=127.0.0.1:6379,abortConnect=false
PilahDok__StorageRoot=/var/lib/pilahdok/jobs
PilahDok__AllowedOrigins__0=https://pilahdok.example.com
PilahDok__AntivirusMode=fail-closed
PilahDok__ClamAvHost=127.0.0.1
PilahDok__ClamAvPort=3310
PilahDok__JobTtlMinutes=60
PilahDok__CleanupIntervalSeconds=30
```

ClamAV perlu listener TCP loopback dan ukuran INSTREAM yang sesuai batas upload. Jangan mengubah fail-closed diam-diam jika scanner tidak siap. Connection string/token tidak boleh dimasukkan ke log atau repository.

5. Buat service `/etc/systemd/system/pilahdok-api.service`:

```ini
[Unit]
Description=PilahDok API
After=network.target redis-server.service

[Service]
User=pilahdok
Group=pilahdok
WorkingDirectory=/opt/pilahdok/release/api
EnvironmentFile=/etc/pilahdok.env
ExecStart=/usr/bin/dotnet /opt/pilahdok/release/api/PilahDok.Api.dll --urls http://127.0.0.1:8080
Restart=on-failure
RestartSec=3
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=/var/lib/pilahdok
UMask=0077
MemoryMax=1G

[Install]
WantedBy=multi-user.target
```

Buat `pilahdok-worker.service` dengan pola sama, `WorkingDirectory=/opt/pilahdok/release/worker`, executable `PilahDok.Worker.dll`, port `8081`, dan `MemoryMax=2G`. API dan worker harus memakai storage absolut yang sama; `PrivateTmp` tidak menjadi storage bersama.

Buat `pilahdok-web.service`:

```ini
[Unit]
Description=PilahDok Web
After=network.target pilahdok-api.service

[Service]
User=pilahdok
Group=pilahdok
WorkingDirectory=/opt/pilahdok/apps/web
Environment=NODE_ENV=production
ExecStart=/usr/bin/node node_modules/next/dist/bin/next start --hostname 127.0.0.1 --port 3000
Restart=on-failure
RestartSec=3
NoNewPrivileges=true
ProtectHome=true
PrivateTmp=true
UMask=0077
MemoryMax=1G

[Install]
WantedBy=multi-user.target
```

6. Nginx harus meneruskan `/api/` ke API dan `/` ke web. Pada server block domain dengan HTTPS yang sudah dikonfigurasi, tambahkan:

```nginx
server_name pilahdok.example.com;
client_max_body_size 50m;
location /api/ {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_request_buffering off;
    proxy_buffering off;
    proxy_read_timeout 180s;
    proxy_send_timeout 180s;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
    access_log off;
}
location / {
    proxy_pass http://127.0.0.1:3000;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-Proto $scheme;
}
```

API mempercayai forwarded headers hanya dari proxy loopback bawaan ASP.NET Core. Jangan membuka port API/worker/Redis ke internet atau menerima forwarded headers dari semua alamat. Jika ada proxy tambahan, konfigurasi daftar proxy terpercaya secara eksplisit.

7. Jalankan `sudo nginx -t`, `sudo systemctl daemon-reload`, lalu `sudo systemctl enable --now pilahdok-api pilahdok-worker pilahdok-web`. Verifikasi readiness API dan worker melalui loopback, kemudian smoke test HTTPS upload sampai download dan delete. Jadikan readiness worker dan cleanup failures sebagai alert; API sehat saja tidak membuktikan worker memproses antrean.

Rujukan hosting dan reverse proxy: [Microsoft: ASP.NET Core dengan Nginx](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-nginx?view=aspnetcore-10.0).

## Pengujian native

```powershell
.\scripts\test-all.ps1
```

Skrip menjalankan restore terkunci, formatter, build solution, backend tests, scan NuGet, npm ci, format/lint/unit web, audit runtime, pemasangan Chromium, build web, dan E2E pada port terpisah 13000/18080/18081/16380. Audit npm lengkap tetap diekspor ke `artifacts/npm-audit.json` dan memberikan warning untuk temuan dev tooling yang belum terselesaikan; tidak diklaim bersih. Tidak memerlukan Docker. Server E2E dihentikan dalam finally. Log ada di `artifacts/native-e2e`. Untuk gate Docker opsional gunakan `-Docker`.

Hentikan launcher lokal sebelum rebuild/test pada Windows agar DLL yang sedang dipakai tidak terkunci. Port test memang terpisah, tetapi build backend menggunakan output project yang sama.
