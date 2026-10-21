import path from "node:path";
import { expect, test } from "@playwright/test";

test("alur utama upload hingga delete", async ({ page }) => {
  await page.goto("/#cara-kerja");
  await expect(page.getByRole("heading", { name: /Pisahkan dokumen/i })).toBeVisible();

  const fixture = path.resolve(
    import.meta.dirname,
    "../../../tests/fixtures/sample-structural.docx",
  );
  await page.locator("#document-upload").setInputFiles(fixture);

  await expect(page.getByText("Analisis selesai")).toBeVisible({ timeout: 60_000 });
  const editorHeading = page.getByRole("heading", { name: "Pilih isi yang ingin dipisahkan" });
  await expect(editorHeading).toBeVisible();
  await expect(page).toHaveURL(/\/$/);
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBeLessThan(100);
  const editorPosition = await editorHeading.boundingBox();
  expect(editorPosition?.x).toBeLessThan(120);
  await expect(page.getByRole("heading", { name: "1. Pilih lokasi potong" })).toBeVisible();
  await expect(page.getByText("BAB I PENDAHULUAN", { exact: true }).first()).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Batalkan potongan sebelum BAB I PENDAHULUAN" }).first(),
  ).toBeVisible();

  await page.getByRole("button", { name: "Pilih sendiri", exact: true }).click();
  await expect(page.getByText(/Belum ada potongan/)).toBeVisible();
  await expect(page.getByRole("button", { name: "Pilih lokasi potong dahulu" })).toBeDisabled();
  await page.getByRole("button", { name: "Per bab utama", exact: true }).click();
  await expect(page.getByRole("img", { name: /Gambar dalam dokumen/ })).toBeVisible();
  await page.screenshot({ path: "../../artifacts/userfriendly-desktop.png", fullPage: true });

  await page.getByText("Ubah nama file 2", { exact: true }).click();
  const secondName = page.getByLabel("Nama file 2", { exact: true });
  await secondName.fill("02-Pendahuluan-Renamed.docx");
  await page
    .getByRole("button", {
      name: "Potong sebelum Isi pendahuluan dengan format tebal.",
      exact: true,
    })
    .click();
  await expect(secondName).toHaveValue("02-Pendahuluan-Renamed.docx");
  await page
    .getByRole("button", {
      name: "Batalkan potongan sebelum Isi pendahuluan dengan format tebal.",
      exact: true,
    })
    .click();
  await expect(secondName).toHaveValue("02-Pendahuluan-Renamed.docx");

  await page.getByRole("button", { name: /Pisahkan menjadi \d+ file/ }).click();
  await expect(page.getByRole("heading", { name: "Dokumen sudah terpilah." })).toBeVisible({
    timeout: 60_000,
  });

  const downloadPromise = page.waitForEvent("download");
  await page.getByRole("button", { name: /Unduh ZIP/ }).click();
  const download = await downloadPromise;
  expect(download.suggestedFilename()).toBe("PilahDok-hasil.zip");

  await page.getByRole("button", { name: "Hapus file saya sekarang" }).click();
  await expect(page.getByText("Job dan seluruh file telah dihapus.")).toBeVisible();
});

test("mobile: editor tetap dapat dipakai tanpa overflow halaman", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/");
  await page
    .locator("#document-upload")
    .setInputFiles(
      path.resolve(import.meta.dirname, "../../../tests/fixtures/sample-structural.docx"),
    );
  await expect(page.getByText("Analisis selesai")).toBeVisible({ timeout: 60_000 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  await page.screenshot({ path: "../../artifacts/userfriendly-mobile.png", fullPage: true });
  await page.getByRole("button", { name: "Hapus sekarang", exact: true }).click();
  await expect(page.getByText("Job dan seluruh file telah dihapus.")).toBeVisible();
});

test("dokumen tanpa Heading: potong langsung dari isi dengan keyboard lalu proses", async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/");
  await page
    .locator("#document-upload")
    .setInputFiles(
      path.resolve(import.meta.dirname, "../../../tests/fixtures/sample-no-headings.docx"),
    );
  await expect(page.getByText("Analisis selesai")).toBeVisible({ timeout: 60_000 });
  await expect(page.getByRole("button", { name: "Pilih sendiri", exact: true })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  await expect(page.getByRole("button", { name: "Per bab utama", exact: true })).toHaveCount(0);
  await expect(
    page.getByText("Bagian awal sebelum heading.", { exact: true }).first(),
  ).toBeVisible();
  await expect(page.getByRole("button", { name: "Pilih lokasi potong dahulu" })).toBeDisabled();
  await page.getByLabel("Cari teks di dokumen", { exact: true }).fill("Butir bernomor");
  await expect(page.getByRole("status").filter({ hasText: "1/1" })).toBeVisible();
  await page.getByLabel("Cari teks di dokumen", { exact: true }).fill("");
  const cut = page.getByRole("button", {
    name: "Potong sebelum Isi pendahuluan dengan format tebal.",
    exact: true,
  });
  await cut.focus();
  await page.keyboard.press("Space");
  await expect(page.getByRole("heading", { name: "File 2", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Pisahkan menjadi 2 file" })).toBeEnabled();
  await page
    .getByRole("button", {
      name: "Batalkan potongan sebelum Isi pendahuluan dengan format tebal.",
      exact: true,
    })
    .press("Space");
  await expect(page.getByRole("button", { name: "Pilih lokasi potong dahulu" })).toBeDisabled();
  await cut.click();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  await page.screenshot({ path: "../../artifacts/userfriendly-manual-mobile.png", fullPage: true });
  await page.getByRole("button", { name: "Lihat 2 file hasil ↓", exact: true }).click();
  await expect(page.getByRole("button", { name: "Pisahkan menjadi 2 file" })).toBeInViewport();
  await page.getByRole("button", { name: "Pisahkan menjadi 2 file" }).click();
  await expect(page.getByRole("heading", { name: "Dokumen sudah terpilah." })).toBeVisible({
    timeout: 60_000,
  });
  await page.getByRole("button", { name: "Hapus file saya sekarang" }).click();
  await expect(page.getByText("Job dan seluruh file telah dihapus.")).toBeVisible();
});

test("file palsu menampilkan error dan tetap dapat dihapus", async ({ page }) => {
  await page.goto("/");
  await page.locator("#document-upload").setInputFiles({
    name: "fake.docx",
    mimeType: "application/octet-stream",
    buffer: Buffer.from("not a docx"),
  });
  await expect(page.getByText("File bukan paket DOCX yang valid.", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Hapus file saya sekarang" }).click();
  await expect(page.getByText("Job dan seluruh file telah dihapus.")).toBeVisible();
});

test("memilih dokumen lain menghapus job sebelumnya", async ({ page }) => {
  await page.goto("/");
  await page
    .locator("#document-upload")
    .setInputFiles(
      path.resolve(import.meta.dirname, "../../../tests/fixtures/sample-structural.docx"),
    );
  await expect(page.getByText("Analisis selesai")).toBeVisible({ timeout: 60_000 });
  const deletion = page.waitForResponse((response) => response.request().method() === "DELETE");
  await page.getByRole("button", { name: "Pilih dokumen lain" }).click();
  expect((await deletion).status()).toBe(204);
  await expect(page.getByRole("heading", { name: "Letakkan DOCX di sini" })).toBeVisible();
});
