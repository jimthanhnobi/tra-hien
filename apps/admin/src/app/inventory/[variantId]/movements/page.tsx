import { notFound } from "next/navigation";
import { AdminScreen } from "../../../../components/admin-screen";
import { isUuid } from "../../../../lib/inventory-forms";

export default async function Page({ params }: { params: Promise<{ variantId: string }> }) {
  const { variantId } = await params;
  if (!isUuid(variantId)) notFound();
  return <AdminScreen path="/inventory" inventoryPage={{ kind: "movements", variantId }} />;
}
