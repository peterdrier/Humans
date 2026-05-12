import { test, expect } from '@playwright/test';
import { loginAsTicketAdmin, loginAsVolunteer, expectBlocked } from '../helpers/auth';

test.describe('Scanner', () => {
  test('ticket admin can decode locally and stop the camera stream', async ({ page }) => {
    const appPosts: string[] = [];
    page.on('request', request => {
      const url = new URL(request.url());
      if (request.method() === 'POST' && url.origin === new URL(page.url()).origin) {
        appPosts.push(url.pathname);
      }
    });

    await page.addInitScript(() => {
      const track = {
        stopCalled: false,
        stop() {
          this.stopCalled = true;
          (window as any).__scannerTrackStopped = true;
        },
      };
      (window as any).__scannerTrackStopped = false;
      (window as any).__scannerTrack = track;

      Object.defineProperty(HTMLMediaElement.prototype, 'srcObject', {
        configurable: true,
        get() {
          return (this as any).__scannerSrcObject ?? null;
        },
        set(value) {
          (this as any).__scannerSrcObject = value;
        },
      });
      HTMLMediaElement.prototype.play = async () => undefined;

      Object.defineProperty(navigator, 'mediaDevices', {
        configurable: true,
        value: {
          getUserMedia: async () => ({
            getTracks: () => [track],
          }),
        },
      });

      (window as any).BarcodeDetector = class {
        async detect() {
          return [{ rawValue: 'https://tickets.example.test/stub/123', format: 'qr_code' }];
        }
      };
    });

    await loginAsTicketAdmin(page);
    await page.goto('/Scanner/Barcode');

    await page.getByRole('button', { name: /scan|escanear|scannen|scanner|scansiona/i }).click();

    await expect(page.locator('#scanner-results')).toContainText('https://tickets.example.test/stub/123');
    expect(appPosts).toEqual([]);

    await page.getByRole('button', { name: /stop|detener|stopp|atura|arrêter|ferma/i }).click();

    await expect.poll(() => page.evaluate(() => (window as any).__scannerTrackStopped)).toBe(true);
  });

  test('volunteer cannot access scanner routes', async ({ page }) => {
    await loginAsVolunteer(page);
    await expectBlocked(page, '/Scanner');
    await expectBlocked(page, '/Scanner/Barcode');
  });
});
