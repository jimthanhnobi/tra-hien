import { notFound } from "next/navigation";
import { AdminScreen } from "../../../../components/admin-screen";
import { productIdPattern } from "../../../../lib/products-api";

export default async function Page({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!productIdPattern.test(id)) notFound();
  return <AdminScreen path="/catalog" productsPage={{ id }} />;
}
