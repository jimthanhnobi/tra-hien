import { API_BASE_PATH } from "@teapremium/api-client";

export default function Home() {
  return (
    <main className="min-h-screen p-8" data-api-base-path={API_BASE_PATH}>
      <h1 className="text-2xl font-semibold">Trà Hiên Admin</h1>
      <p className="mt-4">Nền tảng quản trị đang được xây dựng.</p>
    </main>
  );
}
