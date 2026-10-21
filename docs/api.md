# API PilahDok

Base URL lokal: `http://localhost:8080/api/v1`. Kontrak mesin lengkap tersedia sebagai OpenAPI di `/openapi/v1.json`. Semua respons job memakai `Cache-Control: no-store` dan `X-Correlation-ID`.

## Autentikasi capability per job

Tidak ada login. Endpoint job memerlukan `X-Job-Token`; endpoint download hanya menerima `X-Download-Token` yang berbeda. Token dikembalikan satu kali saat job dibuat dan hanya hash SHA-256 yang disimpan. Kehilangan token berarti job tidak dapat diakses lagi dan akan hilang saat TTL.

## Endpoint

### Buat job

```http
POST /api/v1/jobs
```

Respons `201 Created`:

```json
{
  "jobId": "91b5d5bdb5034cb0af9aa31e26f3277c",
  "accessToken": "<random-base64url>",
  "downloadToken": "<random-base64url-berbeda>",
  "expiresAt": "2026-08-05T11:00:00Z",
  "uploadUrl": "/api/v1/jobs/91b5d5bdb5034cb0af9aa31e26f3277c/document"
}
```

### Upload DOCX

Body adalah byte DOCX mentah agar API dapat melakukan streaming tanpa multipart buffering.

```http
PUT /api/v1/jobs/{jobId}/document
X-Job-Token: <accessToken>
X-File-Name: proposal.docx
Content-Type: application/vnd.openxmlformats-officedocument.wordprocessingml.document

<binary DOCX>
```

`X-File-Name` harus URL-encoded bila mengandung karakter non-ASCII. Respons `200` berisi status `uploaded` setelah validasi package dan antivirus selesai.

### Mulai analisis

```http
POST /api/v1/jobs/{jobId}/analyze
X-Job-Token: <accessToken>
```

Respons `202 Accepted`; poll status sampai `ready` atau `failed`.

### Status job

```http
GET /api/v1/jobs/{jobId}
X-Job-Token: <accessToken>
```

```json
{
  "jobId": "91b5d5bdb5034cb0af9aa31e26f3277c",
  "status": "ready",
  "progress": 100,
  "stage": "ready",
  "createdAt": "2026-08-05T10:00:00Z",
  "updatedAt": "2026-08-05T10:00:02Z",
  "expiresAt": "2026-08-05T11:00:00Z",
  "originalFileName": "proposal.docx",
  "fileSize": 18342,
  "splitMethod": null,
  "publicErrorCode": null,
  "publicErrorMessage": null,
  "downloadAvailable": false,
  "resultSize": null
}
```

Status yang mungkin: `created`, `uploading`, `validating`, `uploaded`, `queued`, `analyzing`, `ready`, `processing`, `completed`, `failed`, `expired`, dan `deleted`.

### Ambil hasil analisis

```http
GET /api/v1/jobs/{jobId}/analysis
X-Job-Token: <accessToken>
```

Respons memuat `metadata`, daftar `paragraphs`, `splitCandidates`, `warnings`, `detectedCapabilities`, dan `analyzedAt`. Setiap candidate memiliki ID opaque, `structureIndex`, tipe (`heading1`, `heading2`, `chapter`, `section`, `pageBreak`, `manual`), judul, confidence, nama usulan, dan warning.

Untuk pembaca isi, setiap paragraf dapat memuat `previewText` (cuplikan lebih panjang daripada `text` judul), dan respons dapat memuat `previewImages: [{ structureIndex, dataUrl }]`. Gambar hanya raster tertanam yang didukung dan dibatasi ukuran/jumlah; tidak ada pengambilan gambar remote. Field bersifat opsional agar analisis lama tetap dapat dibaca. Seluruh respons memerlukan token job dan no-store; teks/gambar pratinjau tidak boleh dicatat ke log. Candidate manual tersedia sebelum blok utuh, termasuk tabel/gambar/content control; bukan di tengah tabel. Capability `visualPagination` tetap false.

### Mulai split

`segments` harus berurutan. `startCandidateId: null` berarti awal dokumen; ID lain wajib berasal dari analysis dan cocok dengan `method`.

```http
POST /api/v1/jobs/{jobId}/split
X-Job-Token: <accessToken>
Content-Type: application/json

{
  "method": "heading1",
  "segments": [
    { "startCandidateId": null, "outputFileName": "01-Bagian-Awal.docx" },
    { "startCandidateId": "heading1-4", "outputFileName": "02-BAB-I.docx" },
    { "startCandidateId": "heading1-12", "outputFileName": "03-BAB-II.docx" }
  ]
}
```

Respons `202 Accepted`; poll sampai `completed` atau `failed`. Jumlah segment 1–200. Server tetap menyaring path/reserved name, menyanitasi filename, dan melakukan deduplikasi nama secara deterministik.

### Download

```http
GET /api/v1/jobs/{jobId}/download
X-Download-Token: <downloadToken>
```

Respons `200 application/zip` bernama `PilahDok-hasil.zip`, mendukung range request, dan memakai header no-store. Download memperpendek expiry ke paling lama 10 menit tanpa memperpanjang hard TTL awal.

### Delete

```http
DELETE /api/v1/jobs/{jobId}
X-Job-Token: <accessToken>
```

Respons `204 No Content`. Directory input/analysis/output/ZIP dan metadata Redis dihapus. Operasi worker yang sedang berlangsung dicegah menghidupkan job kembali melalui optimistic concurrency.

### Capabilities dan health

```text
GET /api/v1/capabilities
GET /health/live
GET /health/ready
```

Capabilities mengungkap format, metode structural, limit upload/TTL, status antivirus, dan status provider visual pagination tanpa janji palsu.

## Error

Error memakai `application/problem+json` bergaya RFC 7807:

```json
{
  "type": "https://pilahdok.local/problems/invalid_docx",
  "title": "invalid_docx",
  "status": 422,
  "detail": "File bukan paket DOCX yang valid.",
  "correlationId": "fda8ad12df5f417da2b71ceda7f3019d"
}
```

Kode penting mencakup `job_not_found`, `job_expired`, `invalid_job_state`, `invalid_docx`, `file_too_large`, `unsupported_file_type`, `macro_not_supported`, `invalid_segments`, `processing_timeout`, `malware_detected`, dan `rate_limit_exceeded`. Detail internal/stack trace tidak dikembalikan.
