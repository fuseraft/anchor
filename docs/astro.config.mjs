// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
	site: 'https://fuseraft.ai',
	base: '/anchor',
	integrations: [
		starlight({
			title: 'anchor',
			description: 'A small coding agent for the terminal, with a deterministic harness that decides what it may touch.',
			logo: { src: './src/assets/anchor.svg' },
			favicon: '/favicon.svg',
			social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/fuseraft/anchor' }],
			editLink: { baseUrl: 'https://github.com/fuseraft/anchor/edit/main/docs/' },
			lastUpdated: true,
			customCss: ['./src/styles/custom.css'],
			sidebar: [
				{
					label: 'Start here',
					items: ['start/install', 'start/quickstart', 'start/configuration'],
				},
				{
					label: 'Guides',
					items: [
						'guides/repl',
						'guides/safety',
						'guides/until',
						'guides/sub-agents',
						'guides/skills',
						'guides/mcp',
						'guides/sessions',
						'guides/scripting',
					],
				},
				{
					label: 'Reference',
					items: [
						'reference/cli',
						'reference/commands',
						'reference/config',
						'reference/tools',
						'reference/json',
					],
				},
				{ label: 'How anchor works', link: '/design/' },
			],
		}),
	],
});
