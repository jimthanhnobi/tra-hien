"use client";

import { useState } from "react";
import Link from "next/link";
import { Check, Copy, ExternalLink } from "lucide-react";
import type { CustomerTag } from "../../lib/customers-api";
import {
  buildSafeZaloUrl,
  contactStatusLabels,
  customerGroupLabels,
  extractExistingCustomerId,
  formatPhoneDisplay,
  isValidHexColor,
  toLocalPhone,
  type CustomerContactStatus,
  type CustomerGroup
} from "../../lib/customers-forms";

export function TagChip({ tag }: { tag: Pick<CustomerTag, "name" | "color"> }) {
  const dotColor = tag.color && isValidHexColor(tag.color) ? tag.color : "#5a7956";
  return (
    <span className="customer-tag-chip">
      <span className="customer-tag-dot" style={{ backgroundColor: dotColor }} aria-hidden="true" />
      <span>{tag.name}</span>
    </span>
  );
}

export function ContactStatusBadge({ status }: { status: CustomerContactStatus | string }) {
  const tone =
    status === "active"
      ? "is-ok"
      : status === "do_not_contact"
        ? "is-warning"
        : status === "erased"
          ? "is-danger"
          : "is-muted";
  const label = contactStatusLabels[status as CustomerContactStatus] ?? status;
  return <span className={`inventory-badge ${tone}`}>{label}</span>;
}

export function CustomerGroupBadge({ group, isVip }: { group: CustomerGroup | string; isVip?: boolean }) {
  const label = customerGroupLabels[group as CustomerGroup] ?? group;
  return (
    <>
      <span className="inventory-badge is-muted">{label}</span>
      {isVip && <span className="customer-vip-badge">VIP</span>}
    </>
  );
}

export function CopyButton({
  value,
  label,
  ariaLabel
}: {
  value: string;
  label: string;
  ariaLabel?: string;
}) {
  const [copied, setCopied] = useState(false);

  async function handleCopy() {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1800);
    } catch {
      // Clipboard API may be unavailable in restricted contexts.
    }
  }

  return (
    <button
      type="button"
      className="inventory-copy-button"
      aria-label={copied ? "Đã sao chép" : (ariaLabel ?? label)}
      onClick={() => void handleCopy()}
    >
      {copied ? <Check size={15} aria-hidden="true" /> : <Copy size={15} aria-hidden="true" />}
      <span>{copied ? "Đã sao chép" : label}</span>
    </button>
  );
}

export function QuickContactStrip({
  phoneE164,
  zaloName,
  email
}: {
  phoneE164: string | null | undefined;
  zaloName: string | null | undefined;
  email?: string | null | undefined;
}) {
  const localPhone = toLocalPhone(phoneE164);
  const zaloUrl = buildSafeZaloUrl(phoneE164);

  return (
    <div className="customer-contact-strip">
      {localPhone ? (
        <div className="customer-contact-item">
          <span className="customer-contact-value">{formatPhoneDisplay(phoneE164)}</span>
          <div className="inventory-inline-actions">
            <CopyButton value={localPhone} label="Sao chép SĐT" ariaLabel={`Sao chép SĐT ${localPhone}`} />
            {zaloUrl && (
              <a
                className="inventory-copy-button"
                href={zaloUrl}
                target="_blank"
                rel="noopener noreferrer"
                aria-label={`Mở Zalo ${localPhone}`}
              >
                <ExternalLink size={15} aria-hidden="true" />
                <span>Mở Zalo</span>
              </a>
            )}
          </div>
        </div>
      ) : (
        <div className="customer-contact-item">
          <span className="hint">Chưa có số điện thoại</span>
        </div>
      )}

      {zaloName && (
        <div className="customer-contact-item">
          <span className="customer-contact-label">Zalo: <strong>{zaloName}</strong></span>
          <CopyButton value={zaloName} label="Sao chép Zalo" ariaLabel={`Sao chép Zalo ${zaloName}`} />
        </div>
      )}

      {email && (
        <div className="customer-contact-item">
          <span className="customer-contact-label">Email: <strong>{email}</strong></span>
        </div>
      )}
    </div>
  );
}

export function DuplicatePhoneLink({ error }: { error: unknown }) {
  const existingId = extractExistingCustomerId(error);
  if (!existingId) return null;
  return (
    <div className="customer-duplicate-link" role="status">
      <span>Số điện thoại này đang thuộc một hồ sơ khách hàng khác.</span>
      <Link className="button secondary" href={`/customers/${existingId}`}>
        Mở hồ sơ khách trùng
      </Link>
    </div>
  );
}
