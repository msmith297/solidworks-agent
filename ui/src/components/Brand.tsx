const base = import.meta.env.BASE_URL;

/** The SolidWorks Agent red cube mark (assets/brand). */
export function BrandMark({ size = 16, className }: { size?: number; className?: string }) {
  const file = size <= 20 ? 32 : size <= 40 ? 64 : 128; // 2x for crisp high-DPI rendering
  return <img src={`${base}brand/mark-${file}.png`} alt="" width={size} height={size} className={className} draggable={false} />;
}

/** Cube + "SolidWorks Agent" lockup. The wordmark is dark graphite; dark themes get a lightened copy. */
export function BrandLogo({ height = 28 }: { height?: number }) {
  return (
    <span className="inline-flex items-center gap-2" aria-label="SolidWorks Agent">
      <BrandMark size={Math.round(height * 1.15)} />
      <img src={`${base}brand/wordmark.png`} alt="" style={{ height: Math.round(height * 0.62) }} className="brand-wordmark" draggable={false} />
    </span>
  );
}
