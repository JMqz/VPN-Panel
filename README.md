# VPN Panel

Aplicativo para Windows que deixa a VPN WireGuard fácil de usar: um clique para conectar, lista de servidores, escolha de quais programas passam pela VPN e aviso quando o servidor para de responder. Funciona com o cliente gratuito [WireSock](https://www.wiresock.net/) e com o arquivo `.conf` de qualquer provedor WireGuard (Proton VPN, Mullvad, Surfshark, IVPN...).

**[⬇ Baixar a versão mais recente](https://github.com/JMqz/VPN-Panel/releases/latest)**

---

## Guia de instalação

Você vai precisar de:

- Windows 10 ou 11;
- uma conta em um provedor de VPN que ofereça **WireGuard** (o plano gratuito da Proton VPN serve);
- uns 10 minutos.

### 1. Instale o VPN Panel

1. Abra a [página de downloads](https://github.com/JMqz/VPN-Panel/releases/latest) e baixe o arquivo **`VPN-Panel-Setup-2.2.exe`**.
2. Abra o arquivo baixado.
3. Se aparecer a tela azul **"O Windows protegeu o computador"**, clique em **Mais informações** e depois em **Executar assim mesmo**. O aviso aparece porque o aplicativo não tem assinatura digital paga.
4. O Windows pede permissão de administrador: clique em **Sim**.
5. Escolha se quer o atalho na área de trabalho e clique em **Instalar**.
6. Clique em **Abrir o VPN Panel**.

O aplicativo fica em **Iniciar → VPN Panel**. O Windows só pede permissão na primeira vez que você abre o aplicativo.

### 2. WireSock (automático)

O WireSock é o programa gratuito (para uso pessoal) que faz a conexão. O VPN Panel controla ele por você.

Você não precisa fazer nada: o instalador do VPN Panel já instala o WireSock junto. Se por algum motivo ele não for instalado, o VPN Panel tenta de novo sozinho quando você abrir, e mostra o andamento na tela **"Vamos deixar tudo pronto"**. Se mesmo assim der errado, aparecem os botões **Tentar de novo** e **Baixar no site** ([wiresock.net](https://www.wiresock.net/)).

### 3. Baixe o arquivo do servidor (`.conf`)

Cada provedor gera um arquivo `.conf` com o servidor e a sua chave de acesso. Na primeira tela, o botão **Pegar no Proton VPN** mostra o passo a passo e abre o site certo. **Outro provedor** mostra as instruções gerais.

**Proton VPN** (também funciona no plano grátis; sem conta, crie uma em [account.protonvpn.com](https://account.protonvpn.com/signup?plan=free))
1. Entre em [account.protonvpn.com](https://account.protonvpn.com/downloads).
2. Vá em **Downloads → Configurações do WireGuard**.
3. Escolha a plataforma **Windows** e um servidor. No plano grátis, use os marcados como gratuitos.
4. Clique em **Criar** e depois em **Baixar**.

**Mullvad**
1. Entre em [mullvad.net](https://mullvad.net/account/wireguard-config) com o número da sua conta.
2. Abra o **Gerador de configuração WireGuard** e escolha **Windows**.
3. Gere uma chave, escolha o país e a cidade e baixe o arquivo.

**Outros provedores:** procure por "WireGuard" ou "configuração manual" no site ou no suporte do provedor.

> ⚠️ O arquivo `.conf` funciona como uma senha. Não compartilhe e não publique.

Dica: baixe arquivos de vários países e adicione todos. Assim você troca de servidor com um clique.

### 4. Adicione o servidor no aplicativo

Na maioria das vezes não precisa fazer nada: com o VPN Panel aberto, o arquivo que chega na pasta **Downloads** entra sozinho. Isso vale enquanto você ainda não tem nenhum servidor, e por 20 minutos depois de abrir o passo a passo. Se o arquivo já estava em Downloads antes, a primeira tela mostra **Adicionar este**.

Outras formas:
- **Já tenho o arquivo** (na primeira tela) ou **Servidores → Adicionar**.
- Arrastar o arquivo para a janela.
- Copiar o arquivo (ou o texto da configuração) e apertar **Ctrl+V** na janela.

O VPN Panel aceita `.conf` e também `.zip` com vários `.conf` dentro (como o da Mullvad).

O servidor aparece na lista com a sigla do país e a cidade.

### 5. Conecte

Na aba **Início**, clique no botão grande:

| O botão fica... | Significa |
|---|---|
| Cinza | Desconectado |
| Girando em âmbar | Conectando ao servidor |
| **Verde** | Conectado e protegido. O servidor confirmou a conexão. |
| Âmbar, "Sem resposta" | O servidor parou de responder. O aplicativo tenta de novo sozinho. |

Abaixo do botão aparece o que está protegido ("Todo o tráfego do PC está protegido") e há quanto tempo você está conectado. O quadro "Seu IP" mostra o endereço que os sites veem.

Pronto, já dá para usar. Os ajustes abaixo são opcionais.

### 6. Ajuste do seu jeito (opcional)

**Escolher quais programas usam a VPN** (aba **Apps**):
- **Todos:** o computador inteiro passa pela VPN. É o mais seguro e o padrão.
- **Só estes:** só os programas da lista usam a VPN; o resto usa a sua internet normal. Por exemplo, só o navegador ou só o Discord.
- **Todos, exceto:** tudo passa pela VPN, menos os programas da lista. Por exemplo, um jogo que fica lento com VPN.

Adicione programas por **Apps abertos**, por **Procurar programa** ou digitando o nome (ex.: `chrome`). As mudanças são salvas sozinhas. Se a VPN estiver ligada, clique em **Reconectar** para aplicá-las.

**Opções** (aba **Opções**):

| Opção | Para que serve |
|---|---|
| Reconectar se a conexão cair | Tenta voltar sozinho até 3 vezes. Vem ligada. |
| Bloquear a internet se a VPN cair | Enquanto a VPN estiver ligada, nada sai do computador sem proteção. |
| Iniciar com o Windows | O aplicativo abre escondido perto do relógio. |
| Conectar ao abrir | Liga a VPN assim que o aplicativo abre. Junto com a opção anterior, você fica protegido desde que liga o computador. |
| Abrir sem pedir permissão | O Windows só pergunta na primeira vez. Vem ligada. |

**Perto do relógio:** fechar a janela não fecha o aplicativo, ele continua no canto da barra de tarefas, perto do relógio. O ícone fica verde quando conectado, âmbar quando o servidor não responde e cinza quando desconectado. Clique com o botão direito no ícone para conectar, desconectar ou sair.

---

## Problemas comuns

| O que aparece | O que fazer |
|---|---|
| **"Sem resposta"** ou **"O servidor não respondeu"** | O servidor pode estar fora do ar, ou a sua rede pode bloquear VPN (comum em redes de empresa, escola e Wi-Fi público). Troque de servidor em **Servidores**. Se nenhum funcionar, teste em outra rede. |
| **"O arquivo deste servidor tem uma chave inválida"** | O arquivo foi alterado ou a chave foi cancelada no site do provedor. Baixe um arquivo novo. |
| **"WireSock não encontrado"**, ou o passo 1 não fica marcado | Clique em **Tentar de novo** no passo 1, ou instale pelo site (veja o passo 2). |
| **"O WireSock não conseguiu preparar a rede"** | Reinstale o WireSock e reinicie o computador. |
| O **IP mostrado continua o meu**, mesmo conectado | É normal no modo **Só estes**: o aplicativo mostra o IP dos programas fora da VPN. No modo **Todos**, deve aparecer o IP do servidor. Clique em ↻ ao lado de "Seu IP" para verificar de novo. |
| **Fiquei sem internet** | Se "Bloquear a internet se a VPN cair" estiver ligada e o servidor parar de responder, a internet fica bloqueada. Clique em **Desconectar** ou troque de servidor. |
| O **antivírus** reclamou do instalador | Acontece com aplicativos sem assinatura digital paga. Baixe sempre pela [página de downloads](https://github.com/JMqz/VPN-Panel/releases) deste repositório. |
| Quero ver o que aconteceu | **Opções → Ver log da conexão**. |

## Desinstalar

**Configurações → Aplicativos → Aplicativos instalados → VPN Panel → Desinstalar.**

A VPN é desconectada e o aplicativo é removido. Os seus servidores e configurações ficam guardados, para o caso de você instalar de novo, a não ser que você ligue **"Apagar meus servidores e configurações"**. O WireSock é um programa separado e pode ser desinstalado do mesmo jeito.

## Sem instalar (versão portátil)

Na [página de downloads](https://github.com/JMqz/VPN-Panel/releases/latest) também tem o **`VpnPanel-2.2-portatil.exe`**. É o mesmo aplicativo, sem instalador e sem atalho no menu Iniciar. Basta abrir.

---

## Para desenvolvedores

### Onde ficam as coisas

| Caminho | O que é |
|---|---|
| `src\` | Código-fonte: `App.cs`, `MainWindow.cs`, `Dialogs.cs`, `Setup.cs`, `MainWindow.xaml`, `Theme.xaml`, `app.manifest`, `setup.manifest`, `build.ps1` |
| `C:\Program Files\VPN Panel\VpnPanel.exe` | Aplicativo instalado (pasta que só administradores alteram) |
| `%APPDATA%\VPN Panel\configs\*.conf` | Servidores importados (contêm chaves privadas) |
| `%APPDATA%\VPN Panel\settings.json` | Servidor ativo e opções |
| `%APPDATA%\VPN Panel\locations.json` | País e cidade de cada servidor, guardados para não consultar de novo |
| `%APPDATA%\VPN Panel\wiresock.log` | Registro do WireSock na última conexão |

### Compilar

```
powershell -NoProfile -ExecutionPolicy Bypass -File src\build.ps1
```

Gera `dist\VpnPanel.exe` e `dist\VPN Panel Setup.exe` usando o compilador `csc.exe` que já vem no Windows (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319`, .NET Framework 4.8), sem instalar nada.

- O compilador é C# 5, então não use sintaxe nova (`$"..."`, `?.`, membros com `=>`).
- O instalador usa o mesmo código, com outro ponto de entrada (`VpnPanel.SetupProgram`), o aplicativo embutido e um manifesto que exige administrador.
- As telas (XAML) e o ícone ficam embutidos e são carregados com `XamlReader`. Os eventos são ligados no código pelo `x:Name`.
- Para atualizar uma cópia instalada, rode o instalador novo ("Reinstalar" ou "Atualizar").

### Como funciona

- **Permissão de administrador:** o executável abre sem privilégios (`asInvoker`).
  - Se a tarefa agendada **VPN Panel - Abrir** existir e apontar para ele, ele a executa pelo Agendador de Tarefas (API COM), e o aplicativo abre com privilégio máximo, sem o aviso do Windows.
  - Se não, ele se reabre pedindo permissão (`runas`).
  - A cópia com privilégio cria ou atualiza essa tarefa. Desligar "Abrir sem pedir permissão" remove a tarefa.
- **Iniciar com o Windows:** tarefa **VPN Panel**, que roda 10 s depois de você entrar no Windows (`VpnPanel.exe --tray`).
- **Instalar o WireSock:** `winget install --id NTKERNEL.WireSockVPNClientCLI --exact --source winget --silent`. Sem winget, baixa o instalador mais recente de wiresock.net, confere a assinatura digital (válida e da The Anti-Cloud Corporation / NT Kernel) e roda com `/S`.
- **Conectar:** `cmd.exe /c wiresock-client.exe run -config <arquivo> -log-level info [-network-lock enabled] > wiresock.log`. Assim o túnel continua ativo mesmo se o aplicativo fechar. Desconectar encerra o processo.
- **Status:** "Conectado" exige a confirmação do servidor (o *handshake* do WireGuard). O aplicativo acompanha o `wiresock.log` procurando as linhas `Handshake initiation sent` (pedido enviado) e `Handshake response received` (servidor respondeu).
  - **15 s** sem resposta: mostra "Sem resposta".
  - **60 s** sem resposta: se a conexão já tinha funcionado, reconecta; se nunca funcionou, derruba o túnel e avisa. Assim o bloqueio de internet nunca fica ativo sem a pessoa saber.
  - Se o WireSock fechar em menos de 10 s, conta como falha, e o aviso traduz o erro registrado.
  - Túneis iniciados fora do aplicativo não têm registro para conferir e aparecem como conectados.
- **Programas na VPN:** grava `AllowedApps` (só estes) ou `DisallowedApps` (todos, exceto) na seção `[Peer]` do `.conf` e mantém todas as outras linhas idênticas, byte a byte. Segundo relatos no fórum do WireSock, `DisallowedApps` falhou em algumas versões.
- **Uma janela só:** o mutex `Global\VpnPanelWireSock` impede uma segunda cópia. Abrir o aplicativo de novo sinaliza `Global\VpnPanelWireSockShow`, e a janela que já está aberta aparece.
- **Desinstalar:** `VpnPanel.exe --uninstall` remove as tarefas, os atalhos, a chave `HKLM\...\Uninstall\VPN Panel` e a pasta em Arquivos de Programas.

### Próximos passos

- Assinatura digital (acaba com o aviso azul do Windows)
- Rodar como serviço do Windows, para conectar antes de entrar na conta
