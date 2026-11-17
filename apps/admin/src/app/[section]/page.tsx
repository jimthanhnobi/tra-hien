import { AdminScreen } from "../../components/admin-screen";
import { navigation } from "../../lib/navigation";
import { notFound } from "next/navigation";

export default async function Section({ params }: { params: Promise<{ section: string }> }) {
  const { section } = await params;
  const path = `/${section}`;
  if (![...navigation.map(item => item.path), "/login", "/change-password", "/totp-setup", "/403"].includes(path)) notFound();
  return <AdminScreen path={path} />;
}
