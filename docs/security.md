# Security PilahDok

## Model ancaman

Input DOCX, seluruh header pengguna, candidate ID, nama output, dan metadata ZIP dianggap tidak tepercaya. Ancaman utama adalah akses lintas-job, brute-force ID/token, ZIP bomb/Zip Slip, XXE/DTD, macro atau embedded archive, filename traversal, resource exhaustion, malicious relationship, malware, data leakage melalui log/cache, dan container escape.

## Kontrol yang diterapkan

- Job ID adalah 128-bit random hex; access/download token random terpisah dan dibandingkan constant-time terhadap hash SHA-256.
- Mengetahui job ID saja tidak memberi akses. Unauthorized/not-found disamarkan agar tidak menjadi oracle job.
- Upload memakai streaming dengan limit di Kestrel, `Content-Length`, dan penghitung byte aktual.
- API menerapkan timeout 30 detik secara default dan policy upload 120 detik; worker memiliki processing timeout terpisah 300 detik. Semua nilai dapat dikonfigurasi dan divalidasi saat startup.
- Ekstensi/MIME bukan satu-satunya validasi: package harus ber-signature ZIP, memiliki `[Content_Types].xml`, `_rels/.rels`, `word/document.xml`, dan Word main content type.
- Semua entry dinormalisasi dan ditolak bila absolute, backslash, URI, drive path, `..`, duplikat berbahaya, atau keluar dari package namespace.
- Batas jumlah entry, ukuran compressed/uncompressed, total uncompressed, dan compression ratio memitigasi decompression bomb.
- Macro, nested archive, embedded OLE/package, encrypted entry, malformed ZIP, dan package terlalu kompleks ditolak.
- XML dibaca dengan `DtdProcessing.Prohibit` dan `XmlResolver=null`; output divalidasi ulang dengan Open XML SDK.
- Filename selalu diperlakukan sebagai label, tidak pernah sebagai path; output dibatasi, disanitasi, diberi `.docx`, dan dideduplikasi deterministik.
- API memakai allowlist CORS, fixed-window rate limit per IP, RFC 7807 tanpa stack trace, security headers, no-store, dan correlation ID acak.
- Log terstruktur hanya memuat job ID, event/code, tipe exception, dan correlation ID; tidak ada isi dokumen, nama file asli, atau token.
- Redis/worker tidak dipublish, backend network bersifat internal, runtime non-root/read-only, capabilities di-drop, `no-new-privileges`, tmpfs dan resource limits aktif.
- Dependency frontend dan NuGet diaudit dalam gate test; versi package/lockfile serta base image digest dipin.

## Antivirus

`IAntivirusScanner` mendukung ClamAV INSTREAM tanpa menyalin konten ke log. Mode:

- `disabled`: hanya untuk local development default;
- `fail-open`: lanjut bila scanner tidak tersedia dan catat warning tanpa konten;
- `fail-closed`: tolak upload bila scanner tidak tersedia; pilihan yang direkomendasikan untuk production.

Compose lokal tidak menyertakan ClamAV agar startup MVP ringan. Production harus menyediakan service scanner privat, mengaktifkan `fail-closed`, dan mengatur limit/timeout di level platform.

## Error dan observability

Public error dipetakan ke pesan aman Bahasa Indonesia. Internal logs menggunakan event ID dan `PublicCode`; exception detail tidak keluar ke respons. Metric ber-cardinality rendah meliputi jumlah upload, bucket ukuran, durasi analysis/process, ukuran output, failure code, serta cleanup count. Platform production harus membatasi siapa yang dapat melihat log dan tidak menambahkan request-body logging/APM capture untuk endpoint dokumen.

## Residual risk dan hardening production

- OOXML valid tetap dapat mengeksploitasi bug parser/library; patch dependency dan image secara rutin, lalu pin digest baru setelah test.
- Cloning package mempertahankan external hyperlink yang sah. PilahDok tidak membuka URL tersebut, tetapi penerima DOCX tetap harus memakai protected view/policy Office.
- Rate limit in-memory API tidak terkoordinasi lintas-replica. Production multi-replica memerlukan edge/WAF atau distributed limiter.
- Local shared volume cocok untuk satu host. Multi-host memerlukan object storage privat, encryption at rest, scoped credentials, dan lifecycle rule.
- TLS berakhir di reverse proxy production; port Compose lokal adalah HTTP development.
- Redis lokal tidak memakai auth karena hanya ada di internal Docker network. Production managed Redis wajib memakai private networking, TLS/auth, ACL, dan tidak boleh dipublish.
- Renderer eksternal tidak dijalankan pada stack saat ini. Bila preview PDF/layout ditambahkan kelak, sandbox, egress deny, timeout, dan file-size limit harus ditinjau ulang.

Laporan kerentanan harus menyertakan langkah reproduksi tanpa dokumen pengguna asli. Jangan memasukkan secret, token hidup, atau file sensitif ke issue maupun repository.

## Privasi dan retensi

### Prinsip

PilahDok tidak memiliki login, profil, histori, analytics pengguna, atau database permanen. Data digunakan hanya untuk menyelesaikan job yang diminta dan tidak dipakai untuk melatih model. UI menjelaskan temporary processing, hard TTL, delete manual, dan batasan pagination sebelum pengguna memproses dokumen.

### Data yang diproses

| Data | Lokasi lokal | Retensi |
|---|---|---|
| DOCX input | `jobs-data/{jobId}/input/source.docx` | Sampai delete atau hard TTL |
| Analysis JSON | directory job | Sampai delete atau hard TTL |
| DOCX hasil dan ZIP | directory job | Sampai delete atau hard TTL; setelah download expiry dipersingkat |
| Metadata job | Redis memory | Sampai delete/expiry; Redis persistence dimatikan |
| Access/download token mentah | Hanya browser/process memory | Tidak disimpan server; hash saja di Redis |
| Structured log | stdout platform | Tanpa isi, nama file asli, atau token; retensi ditentukan operator |

Hard TTL default 60 menit dihitung saat job dibuat dan tidak dapat diperpanjang oleh aktivitas. Download mengubah expiry menjadi nilai yang lebih awal antara TTL yang tersisa dan grace period default 10 menit.

### Mekanisme penghapusan

- Tombol **Hapus file saya sekarang** memanggil endpoint delete untuk menghapus directory job serta metadata Redis.
- Worker menggunakan compare-and-set; hasil operasi yang kehilangan metadata karena delete tidak dapat memulihkan job dan artefaknya dibersihkan.
- Cleanup berjalan saat startup dan setiap 60 detik secara default berdasarkan expiry index.
- Job baru mempunyai marker `expires-at` pada disk tanpa token/konten. Cleanup tetap memakai expiry asli ketika metadata Redis hilang; job lama tanpa marker memakai fallback umur directory.
- Penghapusan fisik terjadi pada sweep berikutnya setelah expiry; ketika worker/server mati, cleanup berlanjut saat worker aktif kembali. Jangan mengartikan hard TTL akses sebagai jaminan penghapusan media ketika semua proses berhenti.
- `docker compose down --volumes` menghapus seluruh volume file lokal secara eksplisit; `docker compose down` biasa mempertahankan volume agar cleanup tetap dapat bekerja setelah restart.

Job yang sudah dihapus atau expired tidak dapat dipulihkan. Delete bersifat logical+physical pada storage aplikasi, tetapi media host/cloud mungkin memiliki snapshot di luar kendali aplikasi. Production harus mengecualikan temporary bucket/volume dari backup atau memastikan snapshot lifecycle tidak melebihi kebijakan privasi.

### Kewajiban operator production

- gunakan region dan processor yang sesuai kebijakan organisasi;
- enkripsi koneksi dan storage, batasi service account ke prefix temporary;
- jangan mengaktifkan body capture, debug dump, request mirroring, atau backup pada data job;
- set lifecycle rule object storage sebagai defense in depth;
- dokumentasikan subprocessor seperti antivirus/layout provider sebelum mengirim dokumen kepadanya;
- batasi retensi log serta pastikan correlation/job ID tidak digabungkan dengan identitas pengguna oleh proxy;
- sediakan prosedur incident response dan verifikasi cleanup berkala.
