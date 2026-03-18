"use client";

export function PublicPageHeader({ title }: { title: string }) {
  return (
    <header className="bg-white shadow">
      <div className="py-6 px-4 sm:px-6 lg:px-8">
        <h1 className="text-3xl font-bold text-gray-900">{title}</h1>
      </div>
    </header>
  );
}
