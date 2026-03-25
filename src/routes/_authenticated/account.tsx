import { createFileRoute } from "@tanstack/react-router"
import { useAuth } from "@/lib/auth"
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar"
import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
} from "@/components/ui/card"
import { Separator } from "@/components/ui/separator"
import {
    IconMail,
    IconPhone,
    IconBriefcase,
    IconBuilding,
    IconId,
    IconShield,
    IconUserCircle,
} from "@tabler/icons-react"

export const Route = createFileRoute("/_authenticated/account")({
    component: AccountPage,
})

function AccountPage() {
    const { user } = useAuth()

    if (!user) return null

    const initials = user.fullName
        .split(" ")
        .map((n) => n[0])
        .join("")
        .toUpperCase()
        .slice(0, 2)

    const avatarUrl = `https://pgas.ph/hris/Content/images/photos/${user.eid}.png`

    const details = [
        { icon: IconId, label: "Employee ID", value: String(user.eid) },
        { icon: IconBriefcase, label: "Position", value: user.position },
        { icon: IconBuilding, label: "Office", value: user.office_name },
        { icon: IconMail, label: "Email", value: user.email },
        { icon: IconPhone, label: "Cellphone", value: user.cellphone || "—" },
        { icon: IconShield, label: "User Level", value: String(user.user_level) },
        { icon: IconUserCircle, label: "User Type", value: String(user.user_type) },
    ]

    return (
        <div className="mx-auto w-full max-w-2xl space-y-6">
            {/* Profile Header */}
            <Card className="overflow-hidden">
                <div className="h-28 bg-gradient-to-r from-primary/80 to-primary/30" />
                <CardContent className="relative pb-6">
                    <div className="-mt-14 flex flex-col items-center gap-3 sm:flex-row sm:items-end sm:gap-5">
                        <Avatar className="size-24 border-4 border-background shadow-lg">
                            <AvatarImage src={avatarUrl} alt={user.fullName} />
                            <AvatarFallback className="text-2xl font-bold">
                                {initials}
                            </AvatarFallback>
                        </Avatar>
                        <div className="text-center sm:text-left sm:pb-1">
                            <h1 className="text-xl font-bold tracking-tight">
                                {user.fullName}
                            </h1>
                            <p className="text-sm text-muted-foreground">{user.position}</p>
                        </div>
                    </div>
                </CardContent>
            </Card>

            {/* Details */}
            <Card>
                <CardHeader>
                    <CardTitle className="text-base">Personal Information</CardTitle>
                </CardHeader>
                <CardContent className="space-y-1">
                    {details.map((item, idx) => (
                        <div key={item.label}>
                            {idx > 0 && <Separator className="my-3" />}
                            <div className="flex items-center gap-3">
                                <div className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-muted">
                                    <item.icon className="size-4 text-muted-foreground" />
                                </div>
                                <div className="min-w-0 flex-1">
                                    <p className="text-xs text-muted-foreground">{item.label}</p>
                                    <p className="truncate text-sm font-medium">{item.value}</p>
                                </div>
                            </div>
                        </div>
                    ))}
                </CardContent>
            </Card>
        </div>
    )
}
