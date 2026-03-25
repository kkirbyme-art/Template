import {
    IconSettings as Settings2,
    IconTerminal2 as SquareTerminal,
} from "@tabler/icons-react"

import { NavMain } from "@/components/nav-main"
import { NavUser } from "@/components/nav-user"
import { SidebarLogo } from "@/components/sidebar-logo"
import {
    Sidebar,
    SidebarContent,
    SidebarFooter,
    SidebarHeader,
    SidebarRail,
} from "@/components/ui/sidebar"
import { useAuth } from "@/lib/auth"

export function AppSidebar({ ...props }: React.ComponentProps<typeof Sidebar>) {
    const { user } = useAuth()

    const navMain = [
        {
            title: "Dashboard",
            url: "/",
            icon: SquareTerminal,
            isActive: true,
        },
        {
            title: "Settings",
            url: "#",
            icon: Settings2,
            items: [
                ...(user?.user_level === 696969
                    ? [{ title: "General", url: "/settings/general" }]
                    : []),
            ],
        },
    ]

    // Only show Settings if it has sub-items
    const filteredNav = navMain.filter(
        (item) => !item.items || item.items.length > 0
    )

    return (
        <Sidebar collapsible="icon" {...props}>
            <SidebarHeader className="bg-primary/10 border-b border-primary/20">
                <SidebarLogo />
            </SidebarHeader>
            <SidebarContent>
                <NavMain items={filteredNav} />
            </SidebarContent>
            <SidebarFooter>
                <NavUser
                    user={{
                        name: user?.fullName ?? "User",
                        email: user?.email ?? "",
                        avatar: user ? `https://pgas.ph/hris/Content/images/photos/${user.eid}.png` : "",
                    }}
                />
            </SidebarFooter>
            <SidebarRail />
        </Sidebar>
    )
}
