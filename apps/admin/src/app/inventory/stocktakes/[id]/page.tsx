import { notFound } from "next/navigation";
import { AdminScreen } from "../../../../components/admin-screen";
import { isUuid } from "../../../../lib/inventory-forms";

export default async function Page({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!isUuid(id)) notFound();
  return <AdminScreen path="/inventory" inventoryPage={{ kind: "stocktake-detail", id }} />;
}
