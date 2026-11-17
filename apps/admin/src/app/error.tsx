"use client";

export default function ErrorPage({ reset }: { reset: () => void }) {
  return <main className="standalone"><h1>Không thể hiển thị trang</h1><p>Vui lòng thử lại. Nếu vẫn gặp lỗi, hãy liên hệ người quản lý.</p><button className="button" onClick={reset}>Thử lại</button></main>;
}
