import { useEffect, useState } from "react";
import { navigate } from "astro:transitions/client";
import { FileText } from "lucide-react";
import {
  CommandDialog,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from "@/components/ui/command";

export interface SearchItem {
  path: string;
  title: string;
  description?: string;
  section: string;
}

interface SearchDialogProps {
  items: SearchItem[];
}

export default function SearchDialog({ items }: SearchDialogProps) {
  const [open, setOpen] = useState(false);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "k" && (event.metaKey || event.ctrlKey)) {
        event.preventDefault();
        setOpen((current) => !current);
      }
    };
    const onOpen = () => setOpen(true);

    document.addEventListener("keydown", onKeyDown);
    document.addEventListener("nanyx:open-search", onOpen);
    return () => {
      document.removeEventListener("keydown", onKeyDown);
      document.removeEventListener("nanyx:open-search", onOpen);
    };
  }, []);

  const grouped = new Map<string, SearchItem[]>();
  for (const item of items) {
    const list = grouped.get(item.section) ?? [];
    list.push(item);
    grouped.set(item.section, list);
  }

  return (
    <CommandDialog open={open} onOpenChange={setOpen}>
      <CommandInput placeholder="Search documentation..." />
      <CommandList>
        <CommandEmpty>No results found.</CommandEmpty>
        {Array.from(grouped.entries()).map(([section, pages]) => (
          <CommandGroup key={section} heading={section}>
            {pages.map((item) => (
              <CommandItem
                key={item.path}
                value={`${item.section} ${item.title} ${item.description ?? ""}`}
                onSelect={() => {
                  setOpen(false);
                  navigate(item.path);
                }}
              >
                <FileText className="mr-2 h-4 w-4 text-muted-foreground" />
                <div>
                  <div className="text-sm font-medium">{item.title}</div>
                  {item.description && (
                    <div className="text-xs text-muted-foreground">{item.description}</div>
                  )}
                </div>
              </CommandItem>
            ))}
          </CommandGroup>
        ))}
      </CommandList>
    </CommandDialog>
  );
}
