const BOOTSTRAPPER_URL =
  "https://autopilotmonitor.blob.core.windows.net/agent/Install-AutopilotMonitor.ps1";

export const runtime = "nodejs";

export async function GET() {
  try {
    const upstream = await fetch(BOOTSTRAPPER_URL, { cache: "no-store" });
    if (!upstream.ok || !upstream.body) {
      return Response.redirect(BOOTSTRAPPER_URL, 302);
    }

    const headers = new Headers();
    headers.set("Content-Type", "application/octet-stream");
    headers.set(
      "Content-Disposition",
      'attachment; filename="Install-AutopilotMonitor.ps1"',
    );
    headers.set("Cache-Control", "public, max-age=300");

    return new Response(upstream.body, {
      status: 200,
      headers,
    });
  } catch {
    return Response.redirect(BOOTSTRAPPER_URL, 302);
  }
}
