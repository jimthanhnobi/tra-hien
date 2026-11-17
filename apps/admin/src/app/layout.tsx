import type { Metadata } from "next";
import "./globals.css";
import { AdminProvider } from "../components/providers";

export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "Trà Hiên Admin",
  description: "Không gian quản trị nội bộ Trà Hiên",
  robots: { index: false, follow: false }
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="vi">
      <body><AdminProvider>{children}</AdminProvider></body>
    </html>
  );
}
