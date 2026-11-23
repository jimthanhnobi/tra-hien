import { AdminScreen } from "../../../../components/admin-screen";
import { isUuid } from "../../../../lib/inventory-forms";

export default async function Page({ searchParams }: { searchParams: Promise<{ variantId?: string }> }) {
  const { variantId } = await searchParams;
  const validVariantId = variantId && isUuid(variantId) ? variantId : undefined;
  return <AdminScreen path="/inventory" inventoryPage={{ kind: "receipt-new", variantId: validVariantId }} />;
}
