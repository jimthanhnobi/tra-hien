import { notFound } from "next/navigation";
import { AdminScreen } from "../../../components/admin-screen";
import type { CatalogSection } from "../../../components/catalog/catalog-screen";

const resources = ["product-types", "categories", "collections", "materials", "artisans"];
export default async function CatalogPage({ params }: { params: Promise<{ resource: string }> }) {
  const { resource } = await params;
  if (!resources.includes(resource)) notFound();
  return <AdminScreen path="/catalog" catalogSection={resource as CatalogSection} />;
}
