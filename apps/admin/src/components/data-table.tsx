"use client";

import { flexRender, getCoreRowModel, useReactTable, type ColumnDef, type PaginationState, type OnChangeFn } from "@tanstack/react-table";
import { FormError, Pending } from "./ui";

// API pages are 1-based; TanStack pageIndex is 0-based. Parent owns server fetching.
export function paginationParams(pagination: PaginationState) {
  return { page: pagination.pageIndex + 1, pageSize: pagination.pageSize };
}

export function DataTable<T>({ data, columns, totalItems, pagination, onPaginationChange, loading, error, onRetry, caption, getRowId }: {
  data: T[]; columns: ColumnDef<T>[]; totalItems: number; pagination: PaginationState;
  onPaginationChange: OnChangeFn<PaginationState>; loading?: boolean; error?: unknown;
  onRetry?: () => void; caption: string; getRowId: (row: T) => string;
}) {
  // TanStack's API returns callable helpers; no React Compiler optimization here.
  // eslint-disable-next-line react-hooks/incompatible-library
  const table = useReactTable({ data, columns, getRowId, getCoreRowModel: getCoreRowModel(),
    manualPagination: true, rowCount: totalItems, state: { pagination }, onPaginationChange });
  if (loading) return <Pending>Đang tải danh sách…</Pending>;
  if (error) return <div><FormError error={error} />{onRetry && <button className="button secondary" onClick={onRetry}>Thử tải lại</button>}</div>;
  return <div className="data-table">
    <div className="table-scroll" role="region" aria-label={caption} tabIndex={0}>
      <table><caption>{caption}</caption><thead>{table.getHeaderGroups().map(group => <tr key={group.id}>
        {group.headers.map(header => <th scope="col" key={header.id}>{header.isPlaceholder ? null : flexRender(header.column.columnDef.header, header.getContext())}</th>)}
      </tr>)}</thead><tbody>{table.getRowModel().rows.length ? table.getRowModel().rows.map(row => <tr key={row.id}>
        {row.getVisibleCells().map(cell => <td key={cell.id}>{flexRender(cell.column.columnDef.cell, cell.getContext())}</td>)}
      </tr>) : <tr><td className="table-empty" colSpan={columns.length}>Chưa có dữ liệu để hiển thị.</td></tr>}</tbody></table>
    </div>
    <div className="pagination"><p role="status">Trang {pagination.pageIndex + 1} / {Math.max(1, table.getPageCount())} · {totalItems} mục</p>
      <label>Số mục <select aria-label="Số mục mỗi trang" value={pagination.pageSize} onChange={event => table.setPagination({ pageIndex: 0, pageSize: Number(event.target.value) })}>
        {[10, 20, 50, 100].map(size => <option key={size} value={size}>{size}</option>)}
      </select></label>
      <div><button className="button secondary" onClick={() => table.previousPage()} disabled={!table.getCanPreviousPage()}>Trang trước</button>
        <button className="button secondary" onClick={() => table.nextPage()} disabled={!table.getCanNextPage()}>Trang sau</button></div>
    </div>
  </div>;
}
