import { IconSignature } from "@tabler/icons-react"
import {
    SidebarMenu,
    SidebarMenuButton,
    SidebarMenuItem,
} from "@/components/ui/sidebar"
import { useSettings } from "@/lib/settings"

export function SidebarLogo() {
    const { logo } = useSettings()

    return (
        <SidebarMenu>
            <SidebarMenuItem>
                <SidebarMenuButton size="lg" className="cursor-default hover:bg-transparent">
                    <div className="flex aspect-square size-8 items-center justify-center rounded-md">
                        {logo ? (
                            <img src={logo} alt="Logo" className="size-8 rounded-md object-contain" />
                        ) : (
                            <div className="flex size-8 items-center justify-center rounded-md bg-primary text-primary-foreground">
                                <IconSignature className="size-5" />
                            </div>
                        )}
                    </div>
                    <div className="grid flex-1 text-left text-sm leading-tight">
                        <span className="truncate font-semibold">DGsign</span>
                        <span className="truncate text-xs opacity-70">Digital Signature</span>
                    </div>
                </SidebarMenuButton>
            </SidebarMenuItem>
        </SidebarMenu>
    )
}
