import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "Trà Hiên",
  description: "Trà Hiên storefront scaffold"
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="vi">
      <body>{children}</body>
    </html>
  );
}
