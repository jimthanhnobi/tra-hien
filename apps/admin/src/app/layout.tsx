import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "Trà Hiên Admin",
  description: "Trà Hiên admin scaffold"
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="vi">
      <body>{children}</body>
    </html>
  );
}
