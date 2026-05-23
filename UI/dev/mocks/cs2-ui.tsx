import React from "react";

// Mock of the cs2/ui module's Button — just a plain styled HTML button.
// `variant="floating"` is the only variant the storyteller uses; we
// approximate it with a circular blue background to match the in-game
// look closely enough for layout work.
export function Button({
  variant,
  children,
  style,
  ...props
}: {
  variant?: string;
  children?: React.ReactNode;
  style?: React.CSSProperties;
} & Omit<React.ButtonHTMLAttributes<HTMLButtonElement>, "style">) {
  const isFloating = variant === "floating";
  const floatingStyle: React.CSSProperties = isFloating
    ? {
        width: "40rem",
        height: "40rem",
        borderRadius: "50%",
        background: "#5bb3e6",
        border: 0,
        padding: 0,
        cursor: "pointer",
        display: "inline-flex",
        alignItems: "center",
        justifyContent: "center",
        color: "#fff",
        margin: "8rem",
      }
    : {};
  return (
    <button {...props} style={{ ...floatingStyle, ...style }}>
      {children}
    </button>
  );
}
