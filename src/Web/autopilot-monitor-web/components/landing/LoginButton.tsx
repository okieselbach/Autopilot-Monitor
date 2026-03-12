"use client";

import { useAuth } from "../../contexts/AuthContext";

export function LoginButton({
  className,
  children,
}: {
  className?: string;
  children: React.ReactNode;
}) {
  const { login } = useAuth();

  return (
    <button onClick={login} className={className}>
      {children}
    </button>
  );
}
