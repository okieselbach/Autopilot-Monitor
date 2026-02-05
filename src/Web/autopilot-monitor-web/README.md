# Autopilot Monitor Web UI

This is the web dashboard for Autopilot Monitor, built with Next.js, React, and Tailwind CSS.

## Prerequisites

- Node.js 18+ and npm (or yarn/pnpm)
- Backend API running (Azure Functions)

## Getting Started

### 1. Install Dependencies

```bash
npm install
# or
yarn install
# or
pnpm install
```

### 2. Configure Environment

Copy the environment template and configure your API endpoint:

```bash
cp .env.local.template .env.local
```

Edit `.env.local` and set your backend API URL:

```env
NEXT_PUBLIC_API_BASE_URL=http://localhost:7071
```

### 3. Run Development Server

```bash
npm run dev
# or
yarn dev
# or
pnpm dev
```

Open [http://localhost:3000](http://localhost:3000) in your browser.

## Project Structure

```
autopilot-monitor-web/
├── app/                    # Next.js App Router
│   ├── layout.tsx         # Root layout
│   ├── page.tsx           # Home page (dashboard)
│   └── globals.css        # Global styles
├── components/            # React components
├── lib/                   # Utility functions and API clients
├── public/               # Static assets
├── .env.local.template   # Environment variables template
├── next.config.ts        # Next.js configuration
├── tailwind.config.ts    # Tailwind CSS configuration
└── package.json          # Dependencies and scripts
```

## Phase 1 Features

The current Phase 1 implementation includes:

- ✅ Basic dashboard layout
- ✅ Stats cards placeholder
- ✅ Responsive design with Tailwind CSS
- ⏳ API integration (to be implemented)
- ⏳ Real-time session monitoring (to be implemented)
- ⏳ Session detail view (to be implemented)

## Development

### Available Scripts

- `npm run dev` - Start development server
- `npm run build` - Build for production
- `npm run start` - Start production server
- `npm run lint` - Run ESLint

### Adding New Features

1. **API Integration**
   - Create API client in `lib/api.ts`
   - Use React hooks for data fetching
   - Consider using SWR or React Query for caching

2. **New Pages**
   - Add routes in `app/` directory
   - Create `page.tsx` for the route
   - Add navigation links

3. **Components**
   - Reusable components go in `components/`
   - Use TypeScript for type safety
   - Style with Tailwind CSS utility classes

## Deployment

### Azure Static Web Apps

1. Create a Static Web App in Azure Portal
2. Connect your GitHub repository
3. Set build configuration:
   - **App location**: `/src/Web/autopilot-monitor-web`
   - **API location**: (leave empty, using separate Functions app)
   - **Output location**: `out` (or leave default)

4. Configure environment variables in Azure:
   - `NEXT_PUBLIC_API_BASE_URL`: Your Azure Functions URL

### Build for Production

```bash
npm run build
```

The output will be in the `.next` directory (or `out` if using static export).

## Troubleshooting

### Port already in use

If port 3000 is already in use:

```bash
PORT=3001 npm run dev
```

### API connection errors

1. Verify the backend is running
2. Check `.env.local` has correct API URL
3. Check browser console for CORS errors
4. Verify Azure Functions is accessible

### Build errors

1. Delete `.next` and `node_modules`
2. Run `npm install` again
3. Run `npm run build`

## Next Steps (Future Phases)

- [ ] Implement real-time updates with SignalR
- [ ] Add session detail pages
- [ ] Create troubleshooting wizard
- [ ] Add fleet analytics dashboard
- [ ] Implement user authentication (Azure AD)
- [ ] Add dark mode toggle
- [ ] Create mobile-responsive design improvements
