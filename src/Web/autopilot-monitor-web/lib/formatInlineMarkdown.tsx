import React from "react";

/**
 * Converts basic inline markdown (**bold** and `code`) to React elements.
 * Returns an array of React nodes suitable for use as JSX children.
 */
export function formatInlineMarkdown(text: string): React.ReactNode[] {
  // Split on **bold** and `code` patterns, keeping the delimiters as capture groups
  const parts = text.split(/(\*\*[^*]+\*\*|`[^`]+`)/g);

  return parts.map((part, i) => {
    if (part.startsWith("**") && part.endsWith("**")) {
      return (
        <strong key={i} className="font-semibold">
          {part.slice(2, -2)}
        </strong>
      );
    }
    if (part.startsWith("`") && part.endsWith("`")) {
      return (
        <code
          key={i}
          className="bg-gray-100 text-gray-800 px-1 py-0.5 rounded text-xs font-mono"
        >
          {part.slice(1, -1)}
        </code>
      );
    }
    return part;
  });
}
