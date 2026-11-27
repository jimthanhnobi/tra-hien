import { notFound } from "next/navigation";
import { AdminScreen } from "../../../components/admin-screen";
import { isUuid } from "../../../lib/customers-forms";

export default async function Page({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!isUuid(id)) notFound();
  return <AdminScreen path="/customers" customersPage={{ kind: "detail", id }} />;
}
